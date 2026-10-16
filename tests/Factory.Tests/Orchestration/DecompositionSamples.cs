using System.Text.Json;
using Factory.Core.Agents;
using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Decomposition;
using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Tests.Architecture;
using Factory.Tests.Reviews;

namespace Factory.Tests.Orchestration;

internal static class DecompositionSamples
{
    // FR-1 (confirmed) and FR-2 (suggested), as in the review samples.
    internal static AnalysisResult Analysis => ArchitectSamples.Request.Analysis!;

    // P1 and P2 are independent; P3 needs both. Classifications cover every tier.
    internal static DecompositionPlan Plan => new("Task entry, persistence and their integration",
    [
        new("P1", "Task entry", "Adding tasks", ["FR-1"], TaskComplexity.Low, TaskImportance.Normal, []),
        new("P2", "Persistence", "Saving tasks", ["FR-2"], TaskComplexity.Medium, TaskImportance.Normal, []),
        new("P3", "Integration", "How entry and persistence connect", ["FR-1", "FR-2"], TaskComplexity.Low, TaskImportance.High, ["P1", "P2"])
    ]);

    internal static string PlanJson => JsonSerializer.Serialize(Plan, DecompositionJson.Options);

    // A minimal proposal that satisfies the Architect validator for exactly the given analysis.
    internal static ArchitectureProposal ProposalFor(AnalysisResult analysis)
    {
        var none = new DesignBasis(true, [], []);
        return new ArchitectureProposal($"Proposal for {analysis.FunctionalRequirements.Count} requirements", ProposalStatus.Proposed,
            analysis.FunctionalRequirements.Where(r => r.Status == RequirementStatus.Confirmed).Select(r => r.Id).ToArray(),
            analysis.FunctionalRequirements.Select(r => new Component("C-" + r.Id, "Component " + r.Id, "Handles " + r.Description,
                new DesignBasis(true, [new RequirementTrace(r.Id, r.Status)], []))).ToArray(),
            [new TechnologyDecision("T-1", "Plain module", "Keep the design small", [new TechnologyAlternative("Separate service", "Isolation", "Operational overhead")],
                ["Simplicity over flexibility"], none)],
            [new ArchitectureRisk("R-1", "Scope may grow", "Review after the first iteration", none)],
            [new ADR("ADR-1", "T-1", "Small first iteration", "Revisit after review")]);
    }

    // Deterministic model for decomposed runs: answers each schema from the request it receives and
    // records part calls and their peak concurrency.
    internal sealed class Provider : IModelProvider
    {
        private int _inside;
        internal int Analysis, Planning, Parts, PeakParallelParts;
        internal string? PlanContent;
        internal bool FailParts;
        // When set, a part call waits (bounded) until this many part calls run at once.
        internal int AwaitParallelParts;
        internal List<string[]> PartRequirementIds { get; } = [];

        public async Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct = default)
        {
            switch (request.OutputSchema!.Name)
            {
                case "analysis_result":
                    Interlocked.Increment(ref Analysis);
                    return new ModelResponse(JsonSerializer.Serialize(DecompositionSamples.Analysis, AnalysisJson.Options), "test");
                case "decomposition_plan":
                    Interlocked.Increment(ref Planning);
                    return new ModelResponse(PlanContent ?? PlanJson, "test");
                default:
                    Interlocked.Increment(ref Parts);
                    var inside = Interlocked.Increment(ref _inside);
                    lock (PartRequirementIds) PeakParallelParts = Math.Max(PeakParallelParts, inside);
                    try
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(10);
                        while (AwaitParallelParts > 1 && Volatile.Read(ref _inside) < AwaitParallelParts && PeakParallelParts < AwaitParallelParts && DateTime.UtcNow < deadline)
                            await Task.Delay(20, ct);
                        lock (PartRequirementIds) PeakParallelParts = Math.Max(PeakParallelParts, Volatile.Read(ref _inside));
                        if (FailParts) throw new InvalidOperationException("synthetic part failure");
                        var input = JsonSerializer.Deserialize<AgentRequest>(request.UserPrompt, ArchitectureJson.Options)!;
                        lock (PartRequirementIds) PartRequirementIds.Add(input.Analysis!.FunctionalRequirements.Select(r => r.Id).ToArray());
                        return new ModelResponse(JsonSerializer.Serialize(ProposalFor(input.Analysis), ArchitectureJson.Options), "test");
                    }
                    finally { Interlocked.Decrement(ref _inside); }
            }
        }
    }

    internal static Factory.Core.Workflows.WorkflowRequest Request(string project) => ReviewSamples.Request with { ProjectId = project };
}
