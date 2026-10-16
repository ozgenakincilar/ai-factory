using System.Diagnostics;
using Factory.Core.Execution;
using Factory.Core.Templates;
using Factory.Infrastructure.Execution;
using Factory.Infrastructure.Templates;

namespace Factory.Tests.Templates;

public class SandboxConfigurationTests
{
    private const string Image = "sha256:0000000000000000000000000000000000000000000000000000000000000001";

    [Fact]
    public void ContainerIsCreatedWithEveryIsolationFlag()
    {
        var args = DockerSandbox.CreateArguments("factory-sbx-test", new DockerSandboxOptions(Image, SandboxLimits.Default));
        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];
        Assert.Equal("none", After("--network"));
        Assert.Contains("--read-only", args);
        Assert.Contains("--init", args);
        Assert.Equal("10001:10001", After("--user"));
        Assert.Equal("ALL", After("--cap-drop"));
        Assert.Equal("no-new-privileges", After("--security-opt"));
        Assert.Equal("none", After("--ipc"));
        Assert.Equal("4096m", After("--memory"));
        Assert.Equal("4096m", After("--memory-swap"));
        Assert.Equal("1024", After("--pids-limit"));
        Assert.Equal("2", After("--cpus"));
        Assert.Contains("/work:rw,exec,nosuid,nodev,size=1024m,uid=10001,gid=10001,mode=0700", args);
        Assert.Contains("/tmp:rw,nosuid,nodev,size=512m,mode=1777", args);
        Assert.Contains("--rm", args);
        // Bounded lifetime: job timeout (20 min) plus grace, so orphaned sandboxes end themselves.
        Assert.Equal([Image, "sleep", "1260"], args.TakeLast(3));
        // Nothing from the host: no bind mounts, volumes, environment, privileges or host namespaces.
        Assert.DoesNotContain(args, a => a is "-v" or "--volume" or "--mount" or "-e" or "--env" or "--env-file" or "--privileged" or "--pid");
        Assert.DoesNotContain(args, a => a.Contains("docker.sock", StringComparison.Ordinal) || a.StartsWith("host", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("factory-runner:dev")]
    [InlineData("factory-runner:latest")]
    [InlineData("sha256:abc")]
    [InlineData("node@sha256:short")]
    [InlineData("")]
    public void RunnerImagesMustBeImmutableReferences(string image) =>
        Assert.Throws<ArgumentException>(() => new DockerSandboxOptions(image, SandboxLimits.Default).Validate());

    [Fact]
    public void DigestReferencesAreAccepted() =>
        new DockerSandboxOptions("mcr.microsoft.com/dotnet/sdk@sha256:" + new string('a', 64), SandboxLimits.Default).Validate();

    [Fact]
    public void LimitsAreValidated()
    {
        SandboxLimits.Default.Validate();
        Assert.Throws<ArgumentException>(() => (SandboxLimits.Default with { Cpus = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (SandboxLimits.Default with { WorkspaceMb = 4096 }).Validate()); // exceeds memory
        Assert.Throws<ArgumentException>(() => (SandboxLimits.Default with { JobTimeout = TimeSpan.FromMinutes(1) }).Validate());
        Assert.Throws<ArgumentException>(() => (SandboxLimits.Default with { OutputBytes = 10 }).Validate());
        Assert.Throws<ArgumentException>(() => (SandboxLimits.Default with { Pids = 8 }).Validate());
    }

    [Fact]
    public void StarterVerificationRunsOfflineStepsForTheGeneratedSolution()
    {
        var steps = StarterVerification.Steps(ApplicationName.Create("StockFlow"));
        Assert.Equal(["dotnet-restore", "dotnet-build", "dotnet-test", "npm-ci", "web-test", "web-build"], steps.Select(s => s.Name));
        Assert.Contains("StockFlow.slnx", steps[0].Arguments);
        Assert.Contains("-p:NuGetAudit=false", steps[0].Arguments);
        Assert.Contains("--offline", steps[3].Arguments);
        Assert.Contains("--ignore-scripts", steps[3].Arguments);
        Assert.Equal("/work/web", steps[5].WorkingDirectory);
    }
}

public sealed class SandboxFactAttribute : FactAttribute
{
    public SandboxFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FACTORY_DOCKER_TESTS") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE")))
            Skip = "Requires Docker and the runner image (FACTORY_DOCKER_TESTS=1, FACTORY_RUNNER_IMAGE; scripts/Build-FactoryRunner.ps1).";
    }
}

// Executed against the real runner image. Each probe is untrusted workspace code.
public class SandboxIsolationTests
{
    private static readonly string Image = Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE") ?? "";

    private static DockerSandbox Sandbox(SandboxLimits? limits = null) => new(new DockerSandboxOptions(Image, limits ?? SandboxLimits.Default));

    private static SandboxLimits Small(int stepSeconds = 30, int memoryMb = 512, int workspaceMb = 64, int pids = 64) =>
        new(1, memoryMb, pids, workspaceMb, 32, TimeSpan.FromSeconds(stepSeconds), TimeSpan.FromSeconds(stepSeconds * 2), 16 * 1024);

    private static TemplateFile[] Script(string body) => [new("probe.sh", body)];

    private static SandboxStep Sh(string name = "probe") => new(name, ["sh", "probe.sh"]);

    private static int LeftoverSandboxes()
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true };
        foreach (var a in new[] { "ps", "--all", "--quiet", "--filter", "label=factory.sandbox=1" }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    [SandboxFact]
    public async Task GeneratedApplication_PassesAllStepsOffline()
    {
        var (template, files) = new FileSystemTemplateCatalog(TemplateRepository.Root()).Load(TemplateRepository.Id, TemplateRepository.Version);
        var name = ApplicationName.Create("SampleShop");
        var application = StarterGenerator.Generate(template, files, name);
        var report = await Sandbox().RunAsync(application.Files, StarterVerification.Steps(name));
        Assert.True(report.Outcome == SandboxOutcome.Passed, string.Join("\n", report.Steps.Select(s => $"{s.Name} {s.Outcome} {s.ExitCode}\n{s.Output}")));
        Assert.All(report.Steps, s => Assert.Equal(StepOutcome.Passed, s.Outcome));
        Assert.Contains("Passed!", report.Steps.Single(s => s.Name == "dotnet-test").Output);
        Assert.Equal(application.ManifestSha256, report.WorkspaceSha256);
        Assert.Equal(Image, report.RunnerImage);
        Assert.Equal(0, LeftoverSandboxes());
    }

    [SandboxFact]
    public async Task UntrustedCode_HasNoNetwork_NoPrivileges_NoHostSecrets_AndCannotWriteOutsideTheWorkspace()
    {
        var canary = "canary-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("FACTORY_SANDBOX_CANARY", canary);
        try
        {
            var report = await Sandbox(Small()).RunAsync(Script("""
                echo "UID=$(id -u)"
                echo "NODE=$(node --version)"
                grep -E '^(NoNewPrivs|CapEff|CapPrm)' /proc/self/status
                if node -e "fetch('http://1.1.1.1',{signal:AbortSignal.timeout(3000)}).then(()=>process.exit(0),()=>process.exit(1))"; then echo NET=OPEN; else echo NET=BLOCKED; fi
                if getent hosts example.com >/dev/null 2>&1; then echo DNS=OPEN; else echo DNS=BLOCKED; fi
                for p in / /etc /usr /usr/bin /opt/factory /opt/factory/nuget /opt/factory/npm /home; do
                  if touch "$p/sandbox-write" 2>/dev/null; then echo "WROTE=$p"; fi
                done
                touch /work/ok && echo WORK=WRITABLE
                if [ -e /var/run/docker.sock ] || [ -e /run/docker.sock ]; then echo DOCKER_SOCKET=PRESENT; else echo DOCKER_SOCKET=ABSENT; fi
                env
                """), [Sh()]);
            var output = report.Steps[0].Output;
            Assert.Equal(SandboxOutcome.Passed, report.Outcome);
            Assert.Contains("UID=10001", output);
            Assert.Contains("NODE=v24.", output); // the network probe below really ran
            Assert.Matches(@"NoNewPrivs:\s+1", output);
            Assert.Matches(@"CapEff:\s+0000000000000000", output);
            Assert.Contains("NET=BLOCKED", output);
            Assert.Contains("DNS=BLOCKED", output);
            Assert.DoesNotContain("WROTE=", output);
            Assert.Contains("WORK=WRITABLE", output);
            Assert.Contains("DOCKER_SOCKET=ABSENT", output);
            Assert.DoesNotContain(canary, output);
            Assert.DoesNotContain("FACTORY_", output);
            Assert.DoesNotContain("OpenAI", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ConnectionStrings", output);
        }
        finally { Environment.SetEnvironmentVariable("FACTORY_SANDBOX_CANARY", null); }
        Assert.Equal(0, LeftoverSandboxes());
    }

    [SandboxFact]
    public async Task FillingTheWorkspace_StopsAtTheDiskLimit()
    {
        var report = await Sandbox(Small(workspaceMb: 64)).RunAsync(Script("""
            dd if=/dev/zero of=/work/fill bs=1M count=200 2>&1
            echo "SIZE=$(du -m /work/fill | cut -f1)"
            exit 0
            """), [Sh()]);
        var output = report.Steps[0].Output;
        Assert.Contains("No space left on device", output);
        var size = int.Parse(System.Text.RegularExpressions.Regex.Match(output, @"SIZE=(\d+)").Groups[1].Value);
        Assert.InRange(size, 1, 64);
        Assert.Equal(0, LeftoverSandboxes());
    }

    [SandboxFact]
    public async Task MemoryExhaustion_IsKilled()
    {
        var report = await Sandbox(Small(memoryMb: 512)).RunAsync(Script("""
            node -e "const a=[];for(;;){a.push(Buffer.alloc(1e7,1))}"
            """), [Sh(), new("after", ["true"])]);
        Assert.Equal(SandboxOutcome.Failed, report.Outcome);
        Assert.Equal(StepOutcome.Failed, report.Steps[0].Outcome);
        Assert.Equal(137, report.Steps[0].ExitCode); // SIGKILL from the cgroup OOM killer
        Assert.Equal(StepOutcome.Skipped, report.Steps[1].Outcome);
        Assert.Equal(0, LeftoverSandboxes());
    }

    [SandboxFact]
    public async Task ForkBomb_IsContainedByThePidLimit_AndTheJobEnds()
    {
        var clock = Stopwatch.StartNew();
        var report = await Sandbox(Small(stepSeconds: 20, pids: 64)).RunAsync(Script("""
            bomb() { bomb | bomb & }
            bomb
            sleep 30
            """), [Sh()]);
        Assert.NotEqual(SandboxOutcome.Passed, report.Outcome);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(90), clock.Elapsed.ToString());
        Assert.Equal(0, LeftoverSandboxes());
    }

    [SandboxFact]
    public async Task EndlessSteps_TimeOut_AndTheSandboxIsRemoved()
    {
        var clock = Stopwatch.StartNew();
        var report = await Sandbox(Small(stepSeconds: 5)).RunAsync(Script("sleep 600"), [Sh(), new("after", ["true"])]);
        Assert.Equal(SandboxOutcome.TimedOut, report.Outcome);
        Assert.Equal(StepOutcome.TimedOut, report.Steps[0].Outcome);
        Assert.Equal(StepOutcome.Skipped, report.Steps[1].Outcome);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), clock.Elapsed.ToString());
        Assert.Equal(0, LeftoverSandboxes());
    }

    [SandboxFact]
    public async Task OutputFloods_AreBounded()
    {
        var report = await Sandbox(Small()).RunAsync(Script("""
            echo FIRST-LINE
            head -c 50000000 /dev/zero | tr '\0' 'a'
            echo
            echo LAST-LINE
            """), [Sh()]);
        var step = report.Steps[0];
        Assert.Equal(StepOutcome.Passed, step.Outcome);
        Assert.True(step.OutputTruncated);
        Assert.InRange(step.Output.Length, 1, 16 * 1024 + 64);
        Assert.StartsWith("FIRST-LINE", step.Output);
        Assert.EndsWith("LAST-LINE\n", step.Output);
    }

    [SandboxFact]
    public async Task SandboxesOrphanedByACrashedWorker_EndThemselves()
    {
        // A worker that dies after start never runs its cleanup; simulate that by never removing.
        var name = "factory-sbx-orphan-" + Guid.NewGuid().ToString("N");
        var limits = Small(stepSeconds: 5) with { JobTimeout = TimeSpan.FromSeconds(5) };
        Assert.Equal(0, Docker([.. DockerSandbox.CreateArguments(name, new DockerSandboxOptions(Image, limits))]));
        Assert.Equal(0, Docker("start", name));
        Assert.Equal(1, LeftoverSandboxes());
        var deadline = DateTime.UtcNow.AddSeconds(5 + DockerSandbox.OrphanGraceSeconds + 30);
        while (LeftoverSandboxes() > 0 && DateTime.UtcNow < deadline) await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(0, LeftoverSandboxes());
    }

    private static int Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    [SandboxFact]
    public async Task ConcurrentJobs_CannotSeeEachOthersFiles()
    {
        var marker = "cross-job-" + Guid.NewGuid().ToString("N");
        var writer = Sandbox(Small()).RunAsync(Script($"""
            echo secret > /work/{marker}
            echo secret > /tmp/{marker}
            sleep 8
            """), [Sh()]);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var reader = await Sandbox(Small()).RunAsync(Script($"""
            if find / -name '{marker}' 2>/dev/null | grep -q .; then echo FOUND; else echo NOT_FOUND; fi
            """), [Sh()]);
        await writer;
        Assert.Contains("NOT_FOUND", reader.Steps[0].Output);
        Assert.Equal(0, LeftoverSandboxes());
    }
}
