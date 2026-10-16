using Factory.Core.Templates;

namespace Factory.Core.Execution;

// M9.2: isolated execution of untrusted workspace code. The workspace (files) is untrusted; the
// steps (commands) and limits are chosen by the Factory, never by the workspace or a model.
public sealed record SandboxLimits(double Cpus, int MemoryMb, int Pids, int WorkspaceMb, int TempMb,
    TimeSpan StepTimeout, TimeSpan JobTimeout, int OutputBytes)
{
    // Memory includes the tmpfs workspace and temp directory, which are charged to the container.
    public static SandboxLimits Default { get; } = new(2, 4096, 1024, 1024, 512,
        TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), 64 * 1024);

    public SandboxLimits Validate()
    {
        if (Cpus is <= 0 or > 16 || MemoryMb is < 256 or > 32768 || Pids is < 32 or > 4096 ||
            WorkspaceMb is < 16 or > 8192 || TempMb is < 16 or > 4096 || WorkspaceMb + TempMb >= MemoryMb ||
            StepTimeout <= TimeSpan.Zero || JobTimeout < StepTimeout || JobTimeout > TimeSpan.FromHours(2) ||
            OutputBytes is < 1024 or > 4 * 1024 * 1024)
            throw new ArgumentException("invalid_sandbox_limits");
        return this;
    }
}

public sealed record SandboxStep(string Name, IReadOnlyList<string> Arguments, string WorkingDirectory = SandboxStep.Root)
{
    public const string Root = "/work";
}

public enum StepOutcome { Passed, Failed, TimedOut, Skipped }

// The sandbox could not be started, written to or used (Docker missing, container gone, faulted).
public sealed class SandboxUnavailableException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public enum SandboxOutcome { Passed, Failed, TimedOut, SandboxError }

// Output is bounded: the first and last half of OutputBytes are kept when a step prints more.
public sealed record StepResult(string Name, StepOutcome Outcome, int? ExitCode, TimeSpan Duration, string Output, bool OutputTruncated);

public sealed record SandboxReport(string RunnerImage, string WorkspaceSha256, SandboxLimits Limits,
    IReadOnlyList<StepResult> Steps, SandboxOutcome Outcome, string? FailureCode, DateTimeOffset StartedAt, TimeSpan Duration);

public interface ISandbox
{
    // Runs the steps in order in a fresh sandbox; the first step that does not pass ends the job and
    // the remaining steps are reported as skipped. The sandbox is always destroyed afterwards.
    Task<SandboxReport> RunAsync(IReadOnlyList<TemplateFile> workspace, IReadOnlyList<SandboxStep> steps,
        CancellationToken cancellationToken = default);
}

public static class StarterVerification
{
    public const string ReportDirectory = SandboxStep.Root + "/.factory-results";

    // Offline verification of an application generated from the starter template. NuGet's
    // vulnerability audit needs the network, so it is off here; dependency scanning is a separate,
    // networked step (M11).
    public static IReadOnlyList<SandboxStep> Steps(ApplicationName name)
    {
        var solution = name.Value + ".slnx";
        return
        [
            new("dotnet-restore", ["dotnet", "restore", solution, "-p:NuGetAudit=false"]),
            new("dotnet-build", ["dotnet", "build", solution, "--no-restore", "-nodeReuse:false", "-p:NuGetAudit=false"]),
            new("dotnet-test", ["dotnet", "test", solution, "--no-build", "--logger", "trx;LogFileName=factory.trx", "--results-directory", ReportDirectory]),
            new("npm-ci", ["npm", "ci", "--offline", "--ignore-scripts"], SandboxStep.Root + "/web"),
            new("web-test", ["npx", "--no-install", "ng", "test", "--watch=false"], SandboxStep.Root + "/web"),
            new("web-build", ["npx", "--no-install", "ng", "build"], SandboxStep.Root + "/web"),
            // M11.1: a Factory command extracts the test counters and per-test outcomes from the report.
            new("test-report", ["sh", "-c", "f=" + ReportDirectory + "/factory.trx; grep -o '<Counters [^>]*>' \"$f\" && " +
                "grep -o '<UnitTestResult [^>]*>' \"$f\" | grep -o 'testName=\"[^\"]*\"\\|outcome=\"[^\"]*\"'"]),
        ];
    }
}
