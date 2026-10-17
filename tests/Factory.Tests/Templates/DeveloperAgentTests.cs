using System.Text.Json;
using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Core.Models;
using Factory.Core.Templates;
using Factory.Infrastructure.Execution;
using Factory.Orchestration.Development;
using Factory.Tests.Architecture;

namespace Factory.Tests.Templates;

internal static class DeveloperScripts
{
    // The test-report step output of a run where the template's tests (and extra) passed.
    internal static string TestReportOutput(string app, int extraPassed = 0, string architectureOutcome = "Passed")
    {
        var tests = ChangeChecks.RequiredTests.Select(t => (Name: $"{app}.Tests.{t}", Outcome: architectureOutcome))
            .Concat(Enumerable.Range(0, 3 + extraPassed).Select(i => (Name: $"{app}.Tests.Other.T{i}", Outcome: "Passed"))).ToList();
        var failed = tests.Count(t => t.Outcome != "Passed");
        return $"<Counters total=\"{tests.Count}\" executed=\"{tests.Count}\" passed=\"{tests.Count - failed}\" failed=\"{failed}\" error=\"0\" />\n" +
            string.Join("\n", tests.Select(t => $"testName=\"{t.Name}\"\noutcome=\"{t.Outcome}\""));
    }

    internal static SandboxReport Report(IReadOnlyList<TemplateFile> workspace, SandboxOutcome outcome, string app = "SampleShop") =>
        new("sha256:" + new string('a', 64), TemplateContent.Hash(TemplateContent.Normalize(workspace)), SandboxLimits.Default,
            outcome == SandboxOutcome.Passed
                ? [new StepResult("dotnet-test", StepOutcome.Passed, 0, TimeSpan.Zero, "Passed!", false),
                   new StepResult(ChangeChecks.TestReportStep, StepOutcome.Passed, 0, TimeSpan.Zero, TestReportOutput(app, 2), false)]
                : [new StepResult("dotnet-test", StepOutcome.Failed, 1, TimeSpan.Zero, "error CS0103: The name 'x' does not exist", false)],
            outcome, outcome == SandboxOutcome.Passed ? null : "step_failed", DateTimeOffset.UnixEpoch, TimeSpan.Zero);

    internal static readonly DevelopmentTask Task = new("Add tasks to a list", ArchitectSamples.Request.Analysis!, ArchitectSamples.Proposal);

    internal static DeveloperAction Write(string path, string content, params string[] ids) => new(DeveloperActionKind.Write, path, content, null, ids, "write");
    internal static DeveloperAction Act(DeveloperActionKind kind, string? path = null, string? text = null) => new(kind, path, null, text, [], null);
    internal static string Turn(params DeveloperAction[] actions) => JsonSerializer.Serialize(new DeveloperTurn("plan", actions), DeveloperJson.Options);

    internal const string TaskItem = """
        using {0}.Domain.Common;

        namespace {0}.Domain.Tasks;

        public sealed class TaskItem : AggregateRoot<Guid>
        {{
            private TaskItem(Guid id, string title) : base(id) => Title = title;

            public string Title {{ get; private set; }}

            public static TaskItem Create(Guid id, string title) =>
                string.IsNullOrWhiteSpace(title) ? throw new DomainException("task_title_required") : new TaskItem(id, title.Trim());
        }}

        """;

    internal const string TaskItemTests = """
        using {0}.Domain.Common;
        using {0}.Domain.Tasks;

        namespace {0}.Tests;

        public class TaskItemTests
        {{
            [Fact]
            public void ATitleIsRequired() => Assert.Equal("task_title_required", Assert.Throws<DomainException>(() => TaskItem.Create(Guid.NewGuid(), " ")).Code);

            [Fact]
            public void TitlesAreTrimmed() => Assert.Equal("Buy milk", TaskItem.Create(Guid.NewGuid(), " Buy milk ").Title);
        }}

        """;
}

// Scripted model: returns the next canned turn and records every prompt it was given.
internal sealed class ScriptedModel(params object[] turns) : IModelProvider
{
    private int _next;
    public List<string> Prompts { get; } = [];
    public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct = default)
    {
        Prompts.Add(request.UserPrompt);
        Assert.Equal("developer_turn", request.OutputSchema!.Name);
        var turn = turns[Math.Min(_next++, turns.Length - 1)];
        return Task.FromResult(turn as ModelResponse ?? new ModelResponse((string)turn, "scripted", Usage: new ModelUsage("openai", "scripted", 5_000, 2_000)));
    }
}

public class DeveloperAgentTests
{
    private sealed class FakeSession : IWorkspaceSession
    {
        public Dictionary<string, string> Written { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<string> Ran { get; } = [];
        public StepOutcome Outcome { get; set; } = StepOutcome.Passed;
        public Task WriteAsync(string path, string content, CancellationToken ct = default) { Written[path] = content; return Task.CompletedTask; }
        public Task DeleteAsync(string path, CancellationToken ct = default) { Deleted.Add(path); return Task.CompletedTask; }
        public Task<StepResult> RunAsync(SandboxStep step, CancellationToken ct = default)
        {
            Ran.Add(step.Name);
            return Task.FromResult(new StepResult(step.Name, Outcome, Outcome == StepOutcome.Passed ? 0 : 1, TimeSpan.Zero, "output of " + step.Name, false));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRunner(FakeSession? session) : IWorkspaceRunner
    {
        public Task<IWorkspaceSession> StartAsync(IReadOnlyList<TemplateFile> files, CancellationToken ct = default)
            => session is null ? throw new SandboxUnavailableException("docker_unavailable") : Task.FromResult<IWorkspaceSession>(session);
    }

    // Returns the given outcomes in order (the last one repeats).
    private sealed class FakeVerifier(params SandboxOutcome[] outcomes) : ISandbox
    {
        private int _next;
        public int Runs => _next;
        public IReadOnlyList<TemplateFile>? Files;
        public Task<SandboxReport> RunAsync(IReadOnlyList<TemplateFile> workspace, IReadOnlyList<SandboxStep> steps, CancellationToken ct = default)
        {
            Files = workspace;
            return Task.FromResult(DeveloperScripts.Report(workspace, outcomes[Math.Min(_next++, outcomes.Length - 1)]));
        }
    }

    // gpt-5.4-mini prices (USD per 1M tokens, verified 2026-10-16) in micro-USD.
    private static CostGuard Guard(long cap = 400_000) => new(750_000, 4_500_000, cap);

    private static (DeveloperAgent Agent, DevelopmentWorkspace Workspace, FakeSession Session, FakeVerifier Verifier) Create(ScriptedModel model,
        DevelopmentLimits? limits = null, SandboxOutcome verification = SandboxOutcome.Passed, long cap = 400_000, bool sandbox = true,
        SandboxOutcome[]? verifications = null)
    {
        var session = new FakeSession();
        var verifier = new FakeVerifier(verifications ?? [verification]);
        return (new DeveloperAgent(model, new FakeRunner(sandbox ? session : null), verifier, Guard(cap), limits ?? DevelopmentLimits.Default),
            new DevelopmentWorkspace(DevelopmentWorkspaceTests.Application("SampleShop")), session, verifier);
    }

    [Fact]
    public async Task EditsBuildsTestsAndFinishes_WithVerifiedFinalFiles()
    {
        var model = new ScriptedModel(
            DeveloperScripts.Turn(DeveloperScripts.Act(DeveloperActionKind.Read, "src/SampleShop.Domain/Common/AggregateRoot.cs"),
                DeveloperScripts.Write("src/SampleShop.Domain/Tasks/TaskItem.cs", string.Format(DeveloperScripts.TaskItem, "SampleShop"), "FR-1", "FR-2"),
                DeveloperScripts.Write("tests/SampleShop.Tests/TaskItemTests.cs", string.Format(DeveloperScripts.TaskItemTests, "SampleShop"), "FR-1"),
                DeveloperScripts.Act(DeveloperActionKind.Build), DeveloperScripts.Act(DeveloperActionKind.Test)),
            DeveloperScripts.Turn(new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "TaskItem with a required title")));
        var (agent, workspace, session, verifier) = Create(model);
        var result = await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.Equal((DevelopmentStatus.Verified, (string?)null, 2, 6), (result.Status, result.FailureCode, result.Turns, result.Actions));
        Assert.Equal(["src/SampleShop.Domain/Tasks/TaskItem.cs", "tests/SampleShop.Tests/TaskItemTests.cs"], result.Changes.Select(c => c.Path));
        Assert.Equal([["FR-1", "FR-2"], ["FR-1"]], result.Changes.Select(c => c.RequirementIds));
        Assert.Equal(["dotnet-restore", "dotnet-build", "dotnet-test"], session.Ran);
        Assert.Equal(2, session.Written.Count);
        // The final verification receives exactly the authoritative files, including the agent's edits.
        Assert.Equal(result.Files.Select(f => f.Path), verifier.Files!.Select(f => f.Path));
        Assert.Contains(verifier.Files!, f => f.Path == "src/SampleShop.Domain/Tasks/TaskItem.cs");
        // The second prompt shows the results of the first turn's actions, including the file read.
        Assert.Contains("public abstract class AggregateRoot<TId>", model.Prompts[1]);
        Assert.Contains("dotnet-test: passed", model.Prompts[1]);
        Assert.Contains("added src/SampleShop.Domain/Tasks/TaskItem.cs", model.Prompts[1]);
        // Each call: 5,000 in / 2,000 out at $0.75/$4.50 per 1M = 3,750 + 9,000 = 12,750 micro-USD.
        Assert.Equal([12_750, 12_750], result.Calls.Select(c => c.CostMicros));
        Assert.Equal(25_500, result.CostMicros);
    }

    [Fact]
    public async Task FilesReadOrWritten_StayVisibleInLaterTurns_WithTheirCurrentContent()
    {
        // Found by the M10.3 acceptance run: showing only the previous turn's results made the model
        // re-read the same files until the action limit.
        var model = new ScriptedModel(
            DeveloperScripts.Turn(DeveloperScripts.Act(DeveloperActionKind.Read, "src/SampleShop.Domain/Common/AggregateRoot.cs")),
            DeveloperScripts.Turn(DeveloperScripts.Write("src/SampleShop.Domain/Note.cs", "namespace SampleShop.Domain;\n// v1\n", "FR-1")),
            DeveloperScripts.Turn(DeveloperScripts.Write("src/SampleShop.Domain/Note.cs", "namespace SampleShop.Domain;\n// v2\n", "FR-1")),
            DeveloperScripts.Turn(new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "done")));
        var (agent, workspace, _, _) = Create(model);
        await agent.RunAsync(DeveloperScripts.Task, workspace);
        foreach (var later in model.Prompts.Skip(2))
            Assert.Contains("public abstract class AggregateRoot<TId>", later);
        Assert.Contains("// v1", model.Prompts[2]);
        Assert.DoesNotContain("// v1", model.Prompts[3]); // only the current content is shown
        Assert.Contains("// v2", model.Prompts[3]);
        Assert.Contains("turn 1:", model.Prompts[3]);    // compact history of earlier turns
        Assert.Contains("read src/SampleShop.Domain/Common/AggregateRoot.cs", model.Prompts[3]);
    }

    [Fact]
    public async Task ReReadingAKnownFile_IsAnsweredWithAReminder()
    {
        var model = new ScriptedModel(
            DeveloperScripts.Turn(DeveloperScripts.Act(DeveloperActionKind.Read, "src/SampleShop.Api/Program.cs")),
            DeveloperScripts.Turn(DeveloperScripts.Act(DeveloperActionKind.Read, "src/SampleShop.Api/Program.cs")),
            DeveloperScripts.Turn(new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "done")));
        var (agent, workspace, _, _) = Create(model);
        await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.Contains("[read src/SampleShop.Api/Program.cs] ok (content under Known files)", model.Prompts[1]);
        Assert.Contains("[read src/SampleShop.Api/Program.cs] already known", model.Prompts[2]);
    }

    // M14 live finding: a model that wrote nothing finished three times and used up its corrections.
    [Fact]
    public async Task FinishingWithoutChanges_IsRefused_WithoutSpendingACorrection()
    {
        var model = new ScriptedModel(
            DeveloperScripts.Turn(new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "nothing to do")),
            DeveloperScripts.Turn(DeveloperScripts.Write("src/SampleShop.Domain/Note.cs", "namespace SampleShop.Domain;\n", "FR-1", "FR-2"),
                new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "done")));
        var (agent, workspace, _, verifier) = Create(model);
        var result = await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.Equal((DevelopmentStatus.Verified, 0, 2), (result.Status, result.Corrections, result.Turns));
        Assert.Contains("[finish] refused: nothing has been written yet", model.Prompts[1]);
        Assert.Equal(1, verifier.Runs);
    }

    // M14 live finding: web unit tests passed while `ng build` failed on template type errors, which the
    // agent saw only in the final verification. With web changes, build now also runs ng build.
    [Fact]
    public async Task BuildingAfterWebChanges_AlsoRunsTheWebBuild()
    {
        var model = new ScriptedModel(
            DeveloperScripts.Turn(DeveloperScripts.Act(DeveloperActionKind.Build)),
            DeveloperScripts.Turn(DeveloperScripts.Write("web/src/app/products.ts", "export const products = [];\n", "FR-1"),
                DeveloperScripts.Act(DeveloperActionKind.Build), DeveloperScripts.Act(DeveloperActionKind.Build)),
            DeveloperScripts.Turn(new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "done")));
        var (agent, workspace, session, _) = Create(model);
        await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.Equal(["dotnet-restore", "dotnet-build", "dotnet-restore", "dotnet-build", "npm-ci", "web-build", "dotnet-restore", "dotnet-build", "web-build"],
            session.Ran);
        Assert.Contains("web-build: passed", model.Prompts[2]);
    }

    // M14 live finding: a lock file larger than the known-files budget was listed as "not shown, read again",
    // and reading it again answered "already known", so the model kept reading and never wrote.
    [Fact]
    public async Task FilesTooLargeToShow_AreNeverClaimedAsKnown()
    {
        var model = new ScriptedModel(
            DeveloperScripts.Turn(DeveloperScripts.Act(DeveloperActionKind.Read, "web/package-lock.json"),
                DeveloperScripts.Act(DeveloperActionKind.Read, "src/SampleShop.Api/Program.cs")),
            DeveloperScripts.Turn(DeveloperScripts.Act(DeveloperActionKind.Read, "web/package-lock.json")),
            DeveloperScripts.Turn(new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "done")));
        var (agent, workspace, _, _) = Create(model);
        await agent.RunAsync(DeveloperScripts.Task, workspace);
        foreach (var prompt in model.Prompts.Skip(1))
        {
            Assert.DoesNotContain("=== web/package-lock.json ===", prompt);
            Assert.DoesNotContain("read again if needed: web/package-lock.json", prompt);
        }
        Assert.Contains("[read web/package-lock.json] too large to show", model.Prompts[1]);
        Assert.Contains("[read web/package-lock.json] too large to show", model.Prompts[2]);
        Assert.Contains("=== src/SampleShop.Api/Program.cs ===", model.Prompts[1]);
    }

    [Fact]
    public async Task AFileLeftOutForSpace_IsNotAnsweredAsAlreadyKnown()
    {
        var model = new ScriptedModel(
            DeveloperScripts.Turn(DeveloperScripts.Act(DeveloperActionKind.Read, "src/SampleShop.Api/Program.cs"),
                DeveloperScripts.Act(DeveloperActionKind.Read, "src/SampleShop.Domain/Common/DomainException.cs"),
                DeveloperScripts.Act(DeveloperActionKind.Read, "src/SampleShop.Domain/Common/IDomainEvent.cs")),
            DeveloperScripts.Turn(DeveloperScripts.Act(DeveloperActionKind.Read, "src/SampleShop.Api/Program.cs")),
            DeveloperScripts.Turn(new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "done")));
        // Each file fits on its own, all three do not: the oldest (Program.cs) is left out of the prompt.
        var (agent, workspace, _, _) = Create(model, DevelopmentLimits.Default with { MaxKnownFileChars = 800 });
        await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.Contains("not shown for space", model.Prompts[1]);
        Assert.Contains("[read src/SampleShop.Api/Program.cs] ok (content under Known files)", model.Prompts[2]);
        Assert.Contains("=== src/SampleShop.Api/Program.cs ===", model.Prompts[2]);
    }

    [Fact]
    public async Task AModelThatOnlyReads_IsToldTheFeatureIsNew_AndRemindedToWrite()
    {
        // Found by the M10.3 acceptance run: the model treated missing feature files as a blocker.
        var looping = new ScriptedModel(DeveloperScripts.Turn(DeveloperScripts.Act(DeveloperActionKind.List)));
        var (agent, workspace, _, _) = Create(looping, DevelopmentLimits.Default with { MaxTurns = 4 });
        await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.All(looping.Prompts, p => Assert.Contains("\"featureCode\":\"none yet: create it\"", p));
        Assert.DoesNotContain("Reminder: you have not written any file yet", looping.Prompts[1]);
        Assert.Contains("Reminder: you have not written any file yet", looping.Prompts[2]);
    }

    [Fact]
    public async Task AFailedVerification_IsReturnedToTheAgent_ThatCorrectsAndIsVerified()
    {
        var model = new ScriptedModel(
            DeveloperScripts.Turn(DeveloperScripts.Write("src/SampleShop.Domain/Note.cs", "namespace SampleShop.Domain;", "FR-1", "FR-2"),
                new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "done, all tests pass")),
            DeveloperScripts.Turn(DeveloperScripts.Write("src/SampleShop.Domain/Note.cs", "namespace SampleShop.Domain;\n", "FR-1", "FR-2"),
                new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "fixed")));
        var (agent, workspace, _, _) = Create(model, verifications: [SandboxOutcome.Failed, SandboxOutcome.Passed]);
        var result = await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.Equal((DevelopmentStatus.Verified, 1, 2), (result.Status, result.Corrections, result.Turns));
        Assert.Contains("NOT VERIFIED (correction 1 of 2)", model.Prompts[1]);
        Assert.Contains("BLOCKING sandbox: dotnet-test failed (exit 1)", model.Prompts[1]);
        Assert.Contains("error CS0103", model.Prompts[1]);
        Assert.Equal("fixed", result.Verdict!.AgentClaim);
    }

    [Fact]
    public async Task VerificationKeepsFailing_EndsAfterTheCorrectionLimit()
    {
        var model = new ScriptedModel(DeveloperScripts.Turn(DeveloperScripts.Write("src/SampleShop.Domain/Note.cs", "x", "FR-1"),
            new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "all good")));
        var (agent, workspace, _, _) = Create(model, verification: SandboxOutcome.Failed);
        var result = await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.Equal((DevelopmentStatus.VerificationFailed, "check_sandbox", 2, 3), (result.Status, result.FailureCode, result.Corrections, result.Turns));
        Assert.Equal("all good", result.Verdict!.AgentClaim); // the claim is recorded, not trusted
    }

    [Fact]
    public async Task UnsafeOrUntracedEdits_AreRejected_AndReportedToTheModel()
    {
        var model = new ScriptedModel(
            DeveloperScripts.Turn(
                DeveloperScripts.Write("tests/SampleShop.Tests/ArchitectureTests.cs", "// disabled", "FR-1"),
                DeveloperScripts.Write("src/SampleShop.Domain/SampleShop.Domain.csproj", "<Project />", "FR-1"),
                DeveloperScripts.Write("src/SampleShop.Domain/Extra.cs", "class Extra {}", "FR-99"),
                DeveloperScripts.Write("src/SampleShop.Domain/Untraced.cs", "class Untraced {}"),
                DeveloperScripts.Write("../escape.cs", "x", "FR-1"),
                DeveloperScripts.Act(DeveloperActionKind.Read, ".git/config")),
            DeveloperScripts.Turn(new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "done")));
        var (agent, workspace, session, _) = Create(model, verification: SandboxOutcome.Failed);
        var result = await agent.RunAsync(DeveloperScripts.Task, workspace);
        // Nothing was accepted, so finishing is refused (no verification is spent) until the turn limit.
        Assert.Equal((DevelopmentStatus.NotFinished, "turn_limit_reached"), (result.Status, result.FailureCode));
        Assert.Empty(result.Changes);
        Assert.Empty(session.Written);
        // Six actions, six rejections: two protected files, an unknown and a missing requirement, two unsafe paths.
        var results = model.Prompts[1][model.Prompts[1].IndexOf("Results of your previous turn's actions:", StringComparison.Ordinal)..];
        results = results[..results.IndexOf("Known files", StringComparison.Ordinal)];
        Assert.Equal(6, results.Split("rejected: workspace_").Length - 1);
        Assert.Contains("rejected: workspace_ProtectedPath", model.Prompts[1]);
        Assert.Contains("rejected: workspace_MissingRequirement", model.Prompts[1]);
        Assert.Contains("rejected: workspace_InvalidPath", model.Prompts[1]);
    }

    [Fact]
    public async Task TheCostCap_StopsCallsBeforeTheyCanExceedIt()
    {
        var guard = Guard(100_000);
        Assert.True(guard.TryReserve(20_000, 12_000, out var first)); // 15,000 + 54,000 = 69,000
        Assert.Equal(69_000, first);
        Assert.False(guard.TryReserve(20_000, 12_000, out _));       // 138,000 would exceed 100,000
        Assert.Equal(3_750 + 4_500, guard.Settle(first, new ModelUsage("openai", "m", 5_000, 1_000)));
        Assert.Equal(8_250, guard.SpentMicros);
        Assert.True(guard.TryReserve(20_000, 12_000, out var unknown));
        Assert.Equal(69_000, guard.Settle(unknown, null)); // no reported usage keeps the worst case

        var looping = new ScriptedModel(DeveloperScripts.Turn(DeveloperScripts.Act(DeveloperActionKind.List)));
        var (agent, workspace, _, _) = Create(looping, cap: 150_000);
        var result = await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.Equal((DevelopmentStatus.BudgetExhausted, "cost_cap_reached"), (result.Status, result.FailureCode));
        Assert.True(result.CostMicros <= 150_000);
        Assert.Equal(looping.Prompts.Count, result.Calls.Count);
    }

    [Fact]
    public async Task TurnAndActionLimits_EndTheJob()
    {
        var looping = new ScriptedModel(DeveloperScripts.Turn(DeveloperScripts.Act(DeveloperActionKind.List)));
        var (agent, workspace, _, _) = Create(looping, DevelopmentLimits.Default with { MaxTurns = 3 });
        var result = await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.Equal((DevelopmentStatus.NotFinished, "turn_limit_reached", 3), (result.Status, result.FailureCode, result.Turns));

        var busy = new ScriptedModel(DeveloperScripts.Turn([.. Enumerable.Repeat(DeveloperScripts.Act(DeveloperActionKind.List), 8)]));
        (agent, workspace, _, _) = Create(busy, DevelopmentLimits.Default with { MaxActions = 10 });
        result = await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.Equal((DevelopmentStatus.NotFinished, "action_limit_reached", 2), (result.Status, result.FailureCode, result.Turns));
    }

    [Theory]
    [InlineData("not json", "invalid_model_output")]
    [InlineData("""{"notes":"x","actions":[]}""", "invalid_model_output")]
    [InlineData("""{"notes":"x","actions":[{"kind":"shell","path":null,"content":null,"text":"rm -rf /","requirementIds":[],"summary":null}]}""", "invalid_model_output")]
    [InlineData("""{"notes":"x","actions":[],"extra":1}""", "invalid_model_output")]
    public async Task MalformedModelOutput_EndsTheJob(string content, string failure)
    {
        var (agent, workspace, _, _) = Create(new ScriptedModel(content));
        var result = await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.Equal((DevelopmentStatus.ModelFailed, failure), (result.Status, result.FailureCode));
    }

    [Fact]
    public async Task RefusalsAndSandboxFailures_EndTheJob()
    {
        var (agent, workspace, _, _) = Create(new ScriptedModel(new ModelResponse("", "m", ModelResponseStatus.Refused)));
        Assert.Equal("model_refused", (await agent.RunAsync(DeveloperScripts.Task, workspace)).FailureCode);
        (agent, workspace, _, _) = Create(new ScriptedModel(DeveloperScripts.Turn(DeveloperScripts.Act(DeveloperActionKind.List))), sandbox: false);
        var result = await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.Equal((DevelopmentStatus.SandboxFailed, 0), (result.Status, result.Calls.Count));
    }
}

// Executed against the real runner image with a scripted model: the full loop on real builds.
[Collection(DockerCollection.Name)]
public class DeveloperAgentSandboxTests
{
    [SandboxFact]
    public async Task AScriptedDeveloper_IsVerifiedOfflineInAFreshSandbox()
    {
        var options = new DockerSandboxOptions(Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE")!, SandboxLimits.Default);
        var model = new ScriptedModel(
            DeveloperScripts.Turn(
                DeveloperScripts.Write("src/SampleShop.Domain/Tasks/TaskItem.cs", string.Format(DeveloperScripts.TaskItem, "SampleShop"), "FR-1"),
                DeveloperScripts.Write("tests/SampleShop.Tests/TaskItemTests.cs", string.Format(DeveloperScripts.TaskItemTests, "SampleShop"), "FR-1"),
                DeveloperScripts.Act(DeveloperActionKind.Build), DeveloperScripts.Act(DeveloperActionKind.Test)),
            DeveloperScripts.Turn(new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "done")));
        var agent = new DeveloperAgent(model, new DockerWorkspaceRunner(options), new DockerSandbox(options), new CostGuard(750_000, 4_500_000, 400_000),
            DevelopmentLimits.Default);
        var workspace = new DevelopmentWorkspace(DevelopmentWorkspaceTests.Application("SampleShop"));
        var result = await agent.RunAsync(DeveloperScripts.Task, workspace);
        Assert.True(result.Status == DevelopmentStatus.Verified, string.Join("\n", model.Prompts.Skip(1)));
        Assert.Equal(7, result.Verification!.Steps.Count(s => s.Outcome == StepOutcome.Passed));
        // M11.1: the Factory's own checks, all passed, with the architecture tests seen in the report.
        Assert.True(result.Verdict!.Verified);
        Assert.Contains(result.Verdict.Checks, c => c.Name == "required_tests" && c.Status == CheckStatus.Passed && c.Detail.Contains("8 of 8"));
        Assert.Contains("Passed!", result.Verification.Steps.Single(s => s.Name == "dotnet-test").Output);
        Assert.True(model.Prompts[1].Contains("dotnet-test: passed"), model.Prompts[1][^Math.Min(3000, model.Prompts[1].Length)..]);
        // The template's 6 tests plus the agent's 2.
        Assert.Matches(@"Passed:\s+8,", result.Verification.Steps.Single(s => s.Name == "dotnet-test").Output);
    }
}
