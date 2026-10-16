using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Factory.Core.Execution;
using Factory.Core.Templates;

namespace Factory.Infrastructure.Execution;

// Image must be an immutable reference: a local image id (sha256:…) or name@sha256:… digest.
public sealed partial record DockerSandboxOptions(string Image, SandboxLimits Limits, string DockerPath = "docker")
{
    public const int RunnerUid = 10001;

    public DockerSandboxOptions Validate()
    {
        if (!ImmutableImage().IsMatch(Image)) throw new ArgumentException("runner_image_not_pinned");
        Limits.Validate();
        return this;
    }

    [GeneratedRegex(@"\A(?:sha256:[0-9a-f]{64}|[a-z0-9][a-z0-9._/:-]*@sha256:[0-9a-f]{64})\z")]
    private static partial Regex ImmutableImage();
}

// One container per job: created with no network, a read-only root filesystem, a non-root user,
// no capabilities, no new privileges, CPU/memory/PID limits and size-limited tmpfs mounts for the
// workspace and temp files. Steps run through `docker exec`, so each exit code comes from the Docker
// daemon rather than from output the untrusted code could forge. The container is always removed,
// by this class or, if the worker dies, by Docker once its bounded lifetime ends.
public sealed class DockerSandbox(DockerSandboxOptions options) : ISandbox
{
    private readonly DockerSandboxOptions _options = options.Validate();

    public const int OrphanGraceSeconds = 60;

    public static IReadOnlyList<string> CreateArguments(string name, DockerSandboxOptions options)
    {
        var l = options.Limits;
        var uid = DockerSandboxOptions.RunnerUid.ToString(CultureInfo.InvariantCulture);
        return
        [
            "create", "--name", name, "--init", "--rm",
            "--network", "none", "--read-only", "--ipc", "none",
            "--user", uid + ":" + uid, "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "--cpus", l.Cpus.ToString(CultureInfo.InvariantCulture),
            "--memory", l.MemoryMb + "m", "--memory-swap", l.MemoryMb + "m",
            "--pids-limit", l.Pids.ToString(CultureInfo.InvariantCulture),
            "--tmpfs", $"/tmp:rw,nosuid,nodev,size={l.TempMb}m,mode=1777",
            "--tmpfs", $"{SandboxStep.Root}:rw,exec,nosuid,nodev,size={l.WorkspaceMb}m,uid={uid},gid={uid},mode=0700",
            "--label", "factory.sandbox=1",
            // The container ends itself after the job timeout, so a sandbox orphaned by a crashed worker
            // (whose cleanup never ran) is stopped and removed (--rm) instead of living forever.
            options.Image, "sleep", ((int)Math.Ceiling(l.JobTimeout.TotalSeconds) + OrphanGraceSeconds).ToString(CultureInfo.InvariantCulture),
        ];
    }

    public async Task<SandboxReport> RunAsync(IReadOnlyList<TemplateFile> workspace, IReadOnlyList<SandboxStep> steps,
        CancellationToken cancellationToken = default)
    {
        var files = TemplateContent.Normalize(workspace);
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var name = "factory-sbx-" + Guid.NewGuid().ToString("N");
        var results = new List<StepResult>();
        SandboxReport Report(SandboxOutcome outcome, string? failure)
        {
            for (var i = results.Count; i < steps.Count; i++) results.Add(new(steps[i].Name, StepOutcome.Skipped, null, TimeSpan.Zero, "", false));
            return new(_options.Image, TemplateContent.Hash(files), _options.Limits, results, outcome, failure, started, clock.Elapsed);
        }
        try
        {
            var limits = _options.Limits;
            if ((await Docker(CreateArguments(name, _options), null, TimeSpan.FromMinutes(2), cancellationToken)).Exit != 0 ||
                (await Docker(["start", name], null, TimeSpan.FromMinutes(1), cancellationToken)).Exit != 0)
                return Report(SandboxOutcome.SandboxError, "sandbox_start_failed");
            var seed = await Docker(["exec", "-i", name, "tar", "-x", "-C", SandboxStep.Root], Archive(files), TimeSpan.FromMinutes(2), cancellationToken);
            if (seed.Exit != 0) return Report(SandboxOutcome.SandboxError, "workspace_seed_failed");
            foreach (var step in steps)
            {
                var remaining = limits.JobTimeout - clock.Elapsed;
                var timeout = remaining < limits.StepTimeout ? remaining : limits.StepTimeout;
                if (timeout <= TimeSpan.Zero) { results.Add(new(step.Name, StepOutcome.TimedOut, null, TimeSpan.Zero, "", false)); return Report(SandboxOutcome.TimedOut, "job_timeout"); }
                var arguments = new List<string> { "exec", "-w", step.WorkingDirectory, "-e", "NO_COLOR=1", "-e", "CI=1", name };
                arguments.AddRange(step.Arguments);
                var stepClock = Stopwatch.StartNew();
                var run = await Docker(arguments, null, timeout, cancellationToken);
                if (run.TimedOut)
                {
                    results.Add(new(step.Name, StepOutcome.TimedOut, null, stepClock.Elapsed, run.Output, run.Truncated));
                    return Report(SandboxOutcome.TimedOut, "step_timeout");
                }
                var passed = run.Exit == 0;
                results.Add(new(step.Name, passed ? StepOutcome.Passed : StepOutcome.Failed, run.Exit, stepClock.Elapsed, run.Output, run.Truncated));
                if (!passed) return Report(SandboxOutcome.Failed, "step_failed");
            }
            return Report(SandboxOutcome.Passed, null);
        }
        catch (System.ComponentModel.Win32Exception) { return Report(SandboxOutcome.SandboxError, "docker_unavailable"); }
        finally
        {
            // Not cancellable: a sandbox must never outlive its job.
            try { await Docker(["rm", "--force", "--volumes", name], null, TimeSpan.FromMinutes(1), CancellationToken.None); }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    // Workspace files as a tar stream: regular files 0644 and directories 0755, no links or devices.
    private static byte[] Archive(IReadOnlyList<TemplateFile> files)
    {
        using var buffer = new MemoryStream();
        using (var tar = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var segments = file.Path.Split('/');
                for (var i = 1; i < segments.Length; i++)
                {
                    var directory = string.Join('/', segments[..i]) + "/";
                    if (directories.Add(directory))
                        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, directory) { Mode = (UnixFileMode)0b111_101_101 });
                }
                var entry = new PaxTarEntry(TarEntryType.RegularFile, file.Path)
                {
                    Mode = (UnixFileMode)0b110_100_100,
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(file.Content)),
                };
                tar.WriteEntry(entry);
            }
        }
        return buffer.ToArray();
    }

    private sealed record ProcessResult(int Exit, bool TimedOut, string Output, bool Truncated);

    private async Task<ProcessResult> Docker(IEnumerable<string> arguments, byte[]? input, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_options.DockerPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("docker_not_started");
        var output = new BoundedOutput(_options.Limits.OutputBytes);
        var stdout = Pump(process.StandardOutput.BaseStream, output);
        var stderr = Pump(process.StandardError.BaseStream, output);
        if (input is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken);
            process.StandardInput.Close();
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var timedOut = false;
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) { await Task.WhenAll(stdout, stderr); throw; }
        }
        await Task.WhenAll(stdout, stderr);
        return new(timedOut ? -1 : process.ExitCode, timedOut, output.Text(), output.Truncated);
    }

    private static async Task Pump(Stream stream, BoundedOutput output)
    {
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0) output.Append(buffer.AsSpan(0, read));
    }

    // Keeps the first half and a rolling last half of the configured size; never grows beyond it.
    private sealed class BoundedOutput(int limit)
    {
        private readonly object _gate = new();
        private readonly MemoryStream _head = new();
        private readonly Queue<byte> _tail = new();
        public bool Truncated { get; private set; }

        public void Append(ReadOnlySpan<byte> data)
        {
            lock (_gate)
            {
                var half = limit / 2;
                var toHead = Math.Min(data.Length, half - (int)_head.Length);
                if (toHead > 0) { _head.Write(data[..toHead]); data = data[toHead..]; }
                foreach (var b in data)
                {
                    _tail.Enqueue(b);
                    if (_tail.Count > half) { _tail.Dequeue(); Truncated = true; }
                }
            }
        }

        public string Text()
        {
            lock (_gate)
            {
                var head = Encoding.UTF8.GetString(_head.ToArray());
                var tail = Encoding.UTF8.GetString(_tail.ToArray());
                return Truncated ? head + "\n…[output truncated]…\n" + tail : head + tail;
            }
        }
    }
}
