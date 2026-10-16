using System.ClientModel;
using System.Text.Json;
using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Infrastructure.Execution;
using Factory.Infrastructure.OpenAI;
using Factory.Orchestration.Development;
using OpenAI.Chat;

namespace Factory.Tests.Templates;

public sealed class LiveDeveloperFactAttribute : FactAttribute
{
    public LiveDeveloperFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FACTORY_LIVE_DEVELOPER") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_LIVE_OPENAI_KEY")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_LIVE_EVIDENCE")))
            Skip = "Paid live run: use scripts/Test-DeveloperAgentLive.ps1 (USD 0.40 per run, USD 2 in total).";
    }
}

// M10.2 live run: one small, approved-style task with gpt-5.4-mini under the USD 0.40 job cap.
// Records the outcome, usage and diff as evidence; it does not assert that the model succeeds.
[Collection(DockerCollection.Name)]
public class LiveDeveloperAgentTests
{
    internal static readonly DevelopmentTask Task = new(
        "Add a Product to the catalog domain with its validation rules.",
        new AnalysisResult("Product catalog rules",
            [new("FR-1", "A product has a name that is required and at most 100 characters; surrounding spaces are removed.", RequirementStatus.Confirmed, "USER-1"),
             new("FR-2", "A product has a price that must not be negative.", RequirementStatus.Confirmed, "USER-2")],
            [], [], []),
        new ArchitectureProposal("A Product aggregate in the domain layer enforces the rules; no persistence or endpoints yet.", ProposalStatus.Proposed,
            ["FR-1", "FR-2"],
            [new("C-1", "Product aggregate", "Holds product name and price and enforces their rules",
                new DesignBasis(true, [new("FR-1", RequirementStatus.Confirmed), new("FR-2", RequirementStatus.Confirmed)], []))],
            [], [], []));

    [LiveDeveloperFact]
    public async Task Gpt54Mini_ImplementsASmallDomainTask_WithinTheCap()
    {
        const string model = "gpt-5.4-mini";
        var client = new ChatClient(model, new ApiKeyCredential(Environment.GetEnvironmentVariable("FACTORY_LIVE_OPENAI_KEY")!),
            new global::OpenAI.OpenAIClientOptions
            {
                RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0),
                NetworkTimeout = TimeSpan.FromSeconds(240),
            });
        var options = new DockerSandboxOptions(Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE")!, SandboxLimits.Default);
        // gpt-5.4-mini: USD 0.75 input / 4.50 output per 1M tokens (verified 2026-10-16); cap USD 0.40.
        var costs = new CostGuard(750_000, 4_500_000, DevelopmentLimits.Default.CostCapMicros);
        var agent = new DeveloperAgent(new OpenAiModelProvider(client, model), new DockerWorkspaceRunner(options), new DockerSandbox(options), costs,
            DevelopmentLimits.Default);
        var workspace = new DevelopmentWorkspace(DevelopmentWorkspaceTests.Application("StockFlow"));
        var started = DateTimeOffset.UtcNow;
        var result = await agent.RunAsync(Task, workspace);
        var diff = result.Changes.Count > 0 ? await GitSnapshot.CreateAsync(options, workspace.BaseFiles, result.Files) : null;
        var evidence = new
        {
            model, started, finished = DateTimeOffset.UtcNow, result.Status, result.FailureCode, result.Turns, result.Actions,
            costMicros = result.CostMicros,
            calls = result.Calls.Select(c => new { c.Turn, c.Status, c.Usage?.Model, c.Usage?.InputTokens, c.Usage?.OutputTokens, c.ReservedMicros, c.CostMicros }),
            changes = result.Changes.Select(c => new { c.Path, c.Kind, c.RequirementIds }),
            verification = result.Verification?.Steps.Select(s => new { s.Name, s.Outcome, s.ExitCode, s.Duration }),
            log = result.Log, diff?.BaseCommit, diff?.FinalCommit, diff?.DiffSha256, diff = diff?.Diff,
        };
        await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("FACTORY_LIVE_EVIDENCE")!,
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions(DeveloperJson.Options) { WriteIndented = true }));
        // The cap is a hard guarantee; success is reported, not assumed.
        Assert.True(result.CostMicros <= DevelopmentLimits.Default.CostCapMicros);
        Assert.All(result.Changes, c => Assert.All(c.RequirementIds, id => Assert.Contains(id, new[] { "FR-1", "FR-2" })));
    }
}
