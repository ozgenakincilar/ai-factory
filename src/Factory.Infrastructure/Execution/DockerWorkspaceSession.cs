using System.Diagnostics;
using System.Text;
using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Core.Templates;

namespace Factory.Infrastructure.Execution;

// M10.1: a sandbox that lives for a whole development job, so the agent can build and test after its
// edits. Same container isolation as DockerSandbox. Files are written only from the Factory's
// authoritative workspace; anything the untrusted code changes in /work is never read back as source.
// A step that times out leaves processes behind, so the session is then faulted and must be disposed.
public sealed class DockerWorkspaceSession : IWorkspaceSession
{
    private readonly DockerSandboxOptions _options;
    private readonly string _name;
    private bool _disposed;

    private DockerWorkspaceSession(DockerSandboxOptions options, string name) { _options = options; _name = name; }

    public bool Faulted { get; private set; }

    public static async Task<DockerWorkspaceSession> StartAsync(DockerSandboxOptions options, IReadOnlyList<TemplateFile> files,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        var session = new DockerWorkspaceSession(options, "factory-sbx-" + Guid.NewGuid().ToString("N"));
        try
        {
            if ((await DockerSandbox.Docker(options, DockerSandbox.CreateArguments(session._name, options), null, TimeSpan.FromMinutes(2), cancellationToken)).Exit != 0 ||
                (await DockerSandbox.Docker(options, ["start", session._name], null, TimeSpan.FromMinutes(1), cancellationToken)).Exit != 0)
                throw new SandboxUnavailableException("sandbox_start_failed");
            await session.SeedAsync(files, cancellationToken);
            return session;
        }
        catch (System.ComponentModel.Win32Exception) { await session.DisposeAsync(); throw new SandboxUnavailableException("docker_unavailable"); }
        catch { await session.DisposeAsync(); throw; }
    }

    // Extracts files into /work (existing files with the same paths are replaced).
    public async Task SeedAsync(IReadOnlyList<TemplateFile> files, CancellationToken cancellationToken = default)
    {
        Ready();
        var seed = await DockerSandbox.Docker(_options, ["exec", "-i", _name, "tar", "-x", "-C", SandboxStep.Root],
            DockerSandbox.Archive(TemplateContent.Normalize(files)), TimeSpan.FromMinutes(2), cancellationToken);
        if (seed.Exit != 0) throw new SandboxUnavailableException("workspace_seed_failed");
    }

    // The path is passed as a positional argument, never interpolated into the shell script.
    public async Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        Ready(path);
        var run = await DockerSandbox.Docker(_options, ["exec", "-i", _name, "sh", "-c", "mkdir -p \"$(dirname \"$1\")\" && cat > \"$1\"", "sh",
            SandboxStep.Root + "/" + path], Encoding.UTF8.GetBytes(content), TimeSpan.FromMinutes(1), cancellationToken);
        if (run.Exit != 0) throw new SandboxUnavailableException("workspace_write_failed");
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        Ready(path);
        var run = await DockerSandbox.Docker(_options, ["exec", _name, "rm", "-f", "--", SandboxStep.Root + "/" + path], null, TimeSpan.FromMinutes(1), cancellationToken);
        if (run.Exit != 0) throw new SandboxUnavailableException("workspace_delete_failed");
    }

    public async Task<StepResult> RunAsync(SandboxStep step, CancellationToken cancellationToken = default)
    {
        Ready();
        var arguments = new List<string> { "exec", "-w", step.WorkingDirectory, "-e", "NO_COLOR=1", "-e", "CI=1", _name };
        arguments.AddRange(step.Arguments);
        var clock = Stopwatch.StartNew();
        var run = await DockerSandbox.Docker(_options, arguments, null, _options.Limits.StepTimeout, cancellationToken);
        if (run.TimedOut)
        {
            Faulted = true;
            return new(step.Name, StepOutcome.TimedOut, null, clock.Elapsed, run.Output, run.Truncated);
        }
        return new(step.Name, run.Exit == 0 ? StepOutcome.Passed : StepOutcome.Failed, run.Exit, clock.Elapsed, run.Output, run.Truncated);
    }

    private void Ready(string? path = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Faulted) throw new SandboxUnavailableException("sandbox_faulted");
        if (path is not null && !WorkspaceRules.IsWorkspacePath(path)) throw new WorkspaceException(WorkspaceError.InvalidPath);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await DockerSandbox.Docker(_options, ["rm", "--force", "--volumes", _name], null, TimeSpan.FromMinutes(1), CancellationToken.None); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

public sealed class DockerWorkspaceRunner(DockerSandboxOptions options) : IWorkspaceRunner
{
    public async Task<IWorkspaceSession> StartAsync(IReadOnlyList<TemplateFile> files, CancellationToken ct = default)
        => await DockerWorkspaceSession.StartAsync(options, files, ct);
}

// Produces the reviewable diff from two trusted file sets in a fresh sandbox where no workspace code
// ever runs: only git, with hooks, external diff drivers and text conversion disabled. Fixed author,
// committer and dates make the commit ids depend on the content only.
public sealed class GitWorkspaceDiffer(DockerSandboxOptions options) : IWorkspaceDiffer
{
    public Task<WorkspaceDiff> CreateAsync(IReadOnlyList<TemplateFile> baseFiles, IReadOnlyList<TemplateFile> finalFiles, CancellationToken ct = default)
        => GitSnapshot.CreateAsync(options, baseFiles, finalFiles, ct);
}

public static class GitSnapshot
{
    public const int MaxDiffBytes = 4 * 1024 * 1024;
    private static readonly string[] Git =
    [
        "env", "HOME=/tmp", "GIT_CONFIG_NOSYSTEM=1", "GIT_AUTHOR_NAME=Universal AI Factory", "GIT_AUTHOR_EMAIL=factory@localhost",
        "GIT_COMMITTER_NAME=Universal AI Factory", "GIT_COMMITTER_EMAIL=factory@localhost",
        "GIT_AUTHOR_DATE=2026-01-01T00:00:00Z", "GIT_COMMITTER_DATE=2026-01-01T00:00:00Z",
        "git", "-c", "core.hooksPath=/dev/null", "-c", "commit.gpgSign=false", "-c", "core.autocrlf=false", "-c", "core.quotePath=false",
    ];

    public static async Task<WorkspaceDiff> CreateAsync(DockerSandboxOptions options, IReadOnlyList<TemplateFile> baseFiles,
        IReadOnlyList<TemplateFile> finalFiles, CancellationToken cancellationToken = default)
    {
        var gitOptions = options with { Limits = options.Limits with { OutputBytes = MaxDiffBytes } };
        await using var session = await DockerWorkspaceSession.StartAsync(gitOptions, baseFiles, cancellationToken);
        async Task<string> Run(string name, params string[] arguments)
        {
            var result = await session.RunAsync(new SandboxStep(name, arguments), cancellationToken);
            if (result.Outcome != StepOutcome.Passed || result.OutputTruncated) throw new SandboxUnavailableException("git_" + name + "_failed");
            return result.Output;
        }
        await Run("init", [.. Git, "init", "-q", "-b", "main"]);
        await Run("base", [.. Git, "add", "-A"]);
        await Run("base-commit", [.. Git, "commit", "-q", "--allow-empty", "-m", "Generated starter application"]);
        await Run("clear", "find", SandboxStep.Root, "-mindepth", "1", "-maxdepth", "1", "!", "-name", ".git", "-exec", "rm", "-rf", "{}", "+");
        await session.SeedAsync(finalFiles, cancellationToken);
        await Run("final", [.. Git, "add", "-A"]);
        await Run("final-commit", [.. Git, "commit", "-q", "--allow-empty", "-m", "Developer Agent changes"]);
        var commits = (await Run("rev-parse", [.. Git, "rev-parse", "HEAD~1", "HEAD"])).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var diff = await Run("diff", [.. Git, "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames", "HEAD~1", "HEAD"]);
        if (commits.Length != 2 || commits.Any(c => c.Length != 40)) throw new SandboxUnavailableException("git_rev_parse_failed");
        return new(commits[0], commits[1], diff, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(diff))));
    }
}
