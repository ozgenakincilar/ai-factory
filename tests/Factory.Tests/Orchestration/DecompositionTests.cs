using System.Text.Json;
using Factory.Core.Agents;
using Factory.Core.Architecture;
using Factory.Core.Decomposition;
using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Orchestration.Agents;
using Factory.Orchestration.Durable;
using Factory.Tests.Architecture;
using Factory.Tests.Reviews;

namespace Factory.Tests.Orchestration;

public class DecompositionTests
{
    private static DecompositionPlan Plan => DecompositionSamples.Plan;
    private static PlanPart P(int index) => Plan.Parts[index];

    public static TheoryData<string, string> InvalidPlans => new()
    {
        { "keys_out_of_order", "invalid_part" },
        { "depends_on_later_part", "invalid_dependency" },
        { "depends_on_itself", "invalid_dependency" },
        { "duplicate_dependency", "invalid_part" },
        { "unknown_requirement", "unknown_requirement" },
        { "missing_requirement", "missing_requirement_coverage" },
        { "no_requirements", "invalid_part" },
        { "too_many_parts", "invalid_plan_structure" },
        { "no_parts", "invalid_plan_structure" },
        { "long_title", "invalid_part" },
        { "unknown_classification", "invalid_part" }
    };

    [Theory]
    [MemberData(nameof(InvalidPlans))]
    public void InvalidPlansAreRejectedWithStaticCodes(string mutation, string code)
    {
        var plan = mutation switch
        {
            "keys_out_of_order" => Plan with { Parts = [P(1), P(0) with { Key = "P2" }, P(2)] },
            "depends_on_later_part" => Plan with { Parts = [P(0) with { DependsOn = ["P2"] }, P(1), P(2)] },
            "depends_on_itself" => Plan with { Parts = [P(0), P(1) with { DependsOn = ["P2"] }, P(2)] },
            "duplicate_dependency" => Plan with { Parts = [P(0), P(1), P(2) with { DependsOn = ["P1", "P1"] }] },
            "unknown_requirement" => Plan with { Parts = [P(0) with { RequirementIds = ["FR-1", "FR-9"] }, P(1), P(2)] },
            "missing_requirement" => Plan with { Parts = [P(0)] },
            "no_requirements" => Plan with { Parts = [P(0) with { RequirementIds = [] }, P(1), P(2)] },
            "too_many_parts" => Plan with { Parts = Enumerable.Range(0, 13).Select(i => P(0) with { Key = DecompositionPlan.Key(i) }).ToArray() },
            "no_parts" => Plan with { Parts = [] },
            "long_title" => Plan with { Parts = [P(0) with { Title = new string('x', 201) }, P(1), P(2)] },
            _ => Plan with { Parts = [P(0) with { Complexity = (TaskComplexity)9 }, P(1), P(2)] }
        };
        Assert.Equal(code, DecompositionPlanValidator.GetError(plan, DecompositionSamples.Analysis));
        Assert.Null(DecompositionPlanValidator.GetError(Plan, DecompositionSamples.Analysis));
    }

    [Fact]
    public void APartSeesOnlyItsRequirementsItsScopeAndItsDependencies()
    {
        var base_ = ArchitectSamples.Request;
        var p1 = DecompositionSamples.ProposalFor(base_.Analysis! with { FunctionalRequirements = [base_.Analysis!.FunctionalRequirements[0]] });
        var scoped = PartScope.Request(base_, P(2), [(P(0), p1)]);
        Assert.Equal(["FR-1", "FR-2"], scoped.Analysis!.FunctionalRequirements.Select(r => r.Id));
        Assert.EndsWith("Design only part P3 - Integration: How entry and persistence connect", scoped.Task);
        Assert.Contains("Dependency P1 (Task entry) proposed: Proposal for 1 requirements", scoped.Context);
        var first = PartScope.Request(base_, P(0), []);
        Assert.Equal(["FR-1"], first.Analysis!.FunctionalRequirements.Select(r => r.Id));
        Assert.Equal(base_.Analysis.Assumptions, first.Analysis.Assumptions);
        Assert.Equal(base_.Context, first.Context);
    }

    [Fact]
    public void MergedPartsFormOneProposalThatPassesTheFullValidator()
    {
        var request = ArchitectSamples.Request;
        var proposals = Plan.Parts.Select(p => DecompositionSamples.ProposalFor(PartScope.Request(request, p, []).Analysis!)).ToArray();
        Assert.All(Plan.Parts.Zip(proposals), x => Assert.True(ArchitectureValidator.IsValid(x.Second, PartScope.Request(request, x.First, []))));
        var merged = ProposalMerger.Merge(Plan, proposals);
        Assert.Null(ArchitectureValidator.GetError(merged, request));
        Assert.Equal(["P1.C-FR-1", "P2.C-FR-2", "P3.C-FR-1", "P3.C-FR-2"], merged.Components.Select(c => c.Id));
        Assert.Equal(["P1.T-1", "P2.T-1", "P3.T-1"], merged.Adrs.Select(a => a.TechnologyDecisionId));
        Assert.Equal(["FR-1"], merged.ConfirmedConstraintRequirementIds);
        Assert.Equal(ProposalStatus.Proposed, merged.Status);
        Assert.Throws<OrchestrationException>(() => ProposalMerger.Merge(Plan, proposals[..2]));
    }

    [Fact]
    public async Task ThePlannerReturnsOnlyValidatedPlans()
    {
        var request = ArchitectSamples.Request;
        var valid = await new PlannerAgent(new ArchitectStub { Content = DecompositionSamples.PlanJson }).ExecuteAsync(request);
        Assert.True(valid.Success);
        Assert.Equal(DecompositionSamples.PlanJson, JsonSerializer.Serialize(valid.Plan, DecompositionJson.Options));
        var stub = new ArchitectStub { Content = JsonSerializer.Serialize(Plan with { Parts = [P(0)] }, DecompositionJson.Options) };
        var uncovered = await new PlannerAgent(stub).ExecuteAsync(request);
        Assert.Equal((false, AgentFailureKind.InvalidOutput), (uncovered.Success, uncovered.FailureKind));
        Assert.Equal("decomposition_plan", stub.LastRequest!.OutputSchema!.Name);
        var noAnalysis = await new PlannerAgent(new ArchitectStub()).ExecuteAsync(request with { Analysis = null });
        Assert.Equal(AgentFailureKind.InvalidRequest, noAnalysis.FailureKind);
    }

    private static WorkflowRun Approved()
    {
        var run = WorkflowRun.Create(Guid.NewGuid(), WorkflowVersions.Decomposition);
        Assert.Equal([StepKind.Analysis, StepKind.Architecture, StepKind.Publication, StepKind.Planning, StepKind.PlanApproval], run.Steps.Select(s => s.Kind));
        Assert.Equal([0], run.Runnable().Select(s => s.Ordinal));
        run.Start(0); run.Succeed(0);
        Assert.Equal([3], run.Runnable().Select(s => s.Ordinal));
        run.Start(3); run.Succeed(3); run.Start(4);
        Assert.Equal(OrchestrationError.InvalidCommand, Assert.Throws<OrchestrationException>(() => run.Succeed(4)).Error);
        run.ApprovePlan(4, Plan);
        return run;
    }

    [Fact]
    public void IndependentPartsRunInParallelAndTheMergeWaitsForAllParts()
    {
        var run = Approved();
        Assert.Equal([5, 6], run.Runnable().Select(s => s.Ordinal));
        run.Start(5); run.Start(6);
        Assert.Empty(run.Runnable());
        run.Succeed(5);
        Assert.Empty(run.Runnable());
        run.Succeed(6);
        Assert.Equal([7], run.Runnable().Select(s => s.Ordinal));
        run.Start(7); run.Succeed(7);
        Assert.Equal([1], run.Runnable().Select(s => s.Ordinal));
        run.Start(1); run.Succeed(1); run.Start(2); run.Succeed(2);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        var restored = WorkflowRun.Restore(run.Id, run.Version, run.Status, null, run.Steps, run.Dependencies);
        Assert.Equal(8, restored.Steps.Count);
        Assert.Throws<OrchestrationException>(() => WorkflowRun.Restore(run.Id, run.Version, run.Status, null, run.Steps,
            run.Dependencies.Where(d => d != new StepDependency(1, 7))));
    }

    [Fact]
    public void AtMostFourAttemptsOfARunRunAtOnce()
    {
        var run = WorkflowRun.Create(Guid.NewGuid(), WorkflowVersions.Decomposition);
        run.Start(0); run.Succeed(0); run.Start(3); run.Succeed(3); run.Start(4);
        var parts = Enumerable.Range(0, 12).Select(i => P(0) with { Key = DecompositionPlan.Key(i) }).ToArray();
        run.ApprovePlan(4, Plan with { Parts = parts });
        Assert.Equal(12, run.Runnable().Count);
        foreach (var ordinal in run.Runnable().Take(4).Select(s => s.Ordinal).ToList()) run.Start(ordinal);
        Assert.Empty(run.Runnable());
        Assert.Equal(OrchestrationError.Conflict, Assert.Throws<OrchestrationException>(() => run.Start(9)).Error);
        // Approval happens once: the approval step is no longer running.
        Assert.Equal(OrchestrationError.Conflict, Assert.Throws<OrchestrationException>(() => run.ApprovePlan(4, Plan)).Error);
    }

    [Fact]
    public void AFailedPartCancelsRunningSiblingsAndADecisionLetsSiblingsFinish()
    {
        var failing = Approved(); failing.Start(5); failing.Start(6);
        failing.Fail(5, "invalid_output");
        Assert.Equal((RunStatus.Failed, StepStatus.Cancelled, StepStatus.Cancelled), (failing.Status, failing.Steps[6].Status, failing.Steps[7].Status));
        var waiting = Approved(); waiting.Start(5); waiting.Start(6);
        waiting.Suspend(5);
        Assert.Equal(RunStatus.Waiting, waiting.Status);
        Assert.Empty(waiting.Runnable());
        waiting.Succeed(6);
        Assert.Equal(RunStatus.Waiting, waiting.Status);
        waiting.Resume(5);
        Assert.Equal(RunStatus.Running, waiting.Status);
        Assert.Equal([5], waiting.Runnable().Select(s => s.Ordinal));
    }

    private static ClaimedStep Claimed(WorkflowRun run, int ordinal, IReadOnlyList<StepDecision>? decisions = null,
        IReadOnlyDictionary<string, ArchitectureProposal>? outputs = null)
        => new(run.Id, Guid.NewGuid(), ordinal, run.Steps[ordinal].Kind, Guid.NewGuid(), ReviewSamples.Request, run.Steps, DecompositionSamples.Analysis, null,
            decisions, 1, run.Version, run.Dependencies, ordinal >= 4 || ordinal == 1 ? Plan : null,
            ordinal >= WorkflowVersions.FirstPartOrdinal ? P(ordinal - WorkflowVersions.FirstPartOrdinal) : null, outputs);

    [Fact]
    public async Task PlanApprovalAsksTheCustomerAndContinuesOnlyAfterApprove()
    {
        var run = WorkflowRun.Create(Guid.NewGuid(), WorkflowVersions.Decomposition);
        run.Start(0); run.Succeed(0); run.Start(3); run.Succeed(3); run.Start(4);
        var provider = new DurableWorkflowProcessorTests.Provider();
        var asked = new DurableWorkflowProcessorTests.Queue { Next = Claimed(run, 4) };
        Assert.Equal(StepOutcome.Suspended, await new DurableWorkflowProcessor(asked, provider).ProcessNextAsync("w", default));
        Assert.Equal("plan_approval", asked.Decision!.Value.Reason);
        Assert.Equal(["approve", "stop"], asked.Decision.Value.Options);
        Assert.False(asked.Approved);
        var approved = new DurableWorkflowProcessorTests.Queue
        {
            Next = Claimed(run, 4, [new StepDecision(Guid.NewGuid(), 4, "plan_approval", ["approve", "stop"], DecisionStatus.Decided, "approve")])
        };
        Assert.Equal(StepOutcome.Completed, await new DurableWorkflowProcessor(approved, provider).ProcessNextAsync("w", default));
        Assert.True(approved.Approved);
        Assert.Equal(0, provider.Calls);
        Assert.Empty(asked.Routes); Assert.Empty(approved.Routes);
    }

    [Theory]
    [InlineData(5, ModelTier.Economy, "role_minimum")]
    [InlineData(6, ModelTier.Standard, "complexity")]
    [InlineData(7, ModelTier.Strong, "high_importance")]
    public async Task PartsAreDesignedInScopeOnTheirRoutedTier(int ordinal, ModelTier tier, string reason)
    {
        var run = Approved();
        if (ordinal == 7) { run.Start(5); run.Succeed(5); run.Start(6); run.Succeed(6); }
        run.Start(ordinal);
        var part = P(ordinal - WorkflowVersions.FirstPartOrdinal);
        var scoped = PartScope.Request(ArchitectSamples.Request, part, []);
        var outputs = ordinal == 7
            ? new Dictionary<string, ArchitectureProposal> { ["P1"] = DecompositionSamples.ProposalFor(PartScope.Request(ArchitectSamples.Request, P(0), []).Analysis!),
                ["P2"] = DecompositionSamples.ProposalFor(PartScope.Request(ArchitectSamples.Request, P(1), []).Analysis!) }
            : null;
        var provider = new DurableWorkflowProcessorTests.Provider
        { Content = JsonSerializer.Serialize(DecompositionSamples.ProposalFor(scoped.Analysis!), ArchitectureJson.Options) };
        var queue = new DurableWorkflowProcessorTests.Queue { Next = Claimed(run, ordinal, outputs: outputs) };
        Assert.Equal(StepOutcome.Completed, await new DurableWorkflowProcessor(queue, provider, new ModelIdentity("openai", "m")).ProcessNextAsync("w", default));
        Assert.Equal(part.RequirementIds, queue.Part!.Components.Select(c => c.Id[2..]));
        Assert.Equal(new ModelRoute(tier, reason, ModelRoutingPolicy.Version), Assert.Single(queue.Routes).Route);
    }

    [Fact]
    public async Task TheMergeStepCombinesApprovedPartsWithoutAModelCall()
    {
        var run = Approved();
        foreach (var ordinal in new[] { 5, 6, 7 }) { run.Start(ordinal); run.Succeed(ordinal); }
        run.Start(1);
        var outputs = Plan.Parts.ToDictionary(p => p.Key, p => DecompositionSamples.ProposalFor(PartScope.Request(ArchitectSamples.Request, p, []).Analysis!));
        var provider = new DurableWorkflowProcessorTests.Provider();
        var queue = new DurableWorkflowProcessorTests.Queue { Next = Claimed(run, 1, outputs: outputs) };
        Assert.Equal(StepOutcome.Completed, await new DurableWorkflowProcessor(queue, provider, new ModelIdentity("openai", "m")).ProcessNextAsync("w", default));
        Assert.Equal(0, provider.Calls); Assert.Empty(queue.Routes); Assert.Empty(queue.Reservations);
        Assert.Equal(ProposalMerger.Merge(Plan, Plan.Parts.Select(p => outputs[p.Key]).ToArray()).Components, queue.Architecture!.Components);
        // A missing part output is an integrity failure, never a partial merge.
        var missing = new DurableWorkflowProcessorTests.Queue { Next = Claimed(run, 1, outputs: outputs.Where(o => o.Key != "P2").ToDictionary()) };
        await Assert.ThrowsAsync<OrchestrationException>(() => new DurableWorkflowProcessor(missing, provider).ProcessNextAsync("w", default));
        Assert.Null(missing.Architecture);
    }
}
