using Factory.Core.Orchestration;

namespace Factory.Tests.Orchestration;

public class WorkflowRunTests
{
    private static WorkflowRun New() => WorkflowRun.Create(Guid.NewGuid(), DurableWorkflowLimits.WorkflowVersion);

    [Fact]
    public void CreatePlansAnalysisArchitecturePublicationInOrder()
    {
        var run = New();
        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Equal([StepKind.Analysis, StepKind.Architecture, StepKind.Publication], run.Steps.Select(s => s.Kind));
        Assert.All(run.Steps, s => { Assert.Equal(StepStatus.Pending, s.Status); Assert.Equal(0, s.Attempts); });
        Assert.Equal(0, run.NextRunnable()!.Ordinal);
    }

    [Fact]
    public void StepsRunStrictlyInOrderAndOneAtATime()
    {
        var run = New();
        Assert.Equal(OrchestrationError.Conflict, Assert.Throws<OrchestrationException>(() => run.Start(1)).Error);
        run.Start(0);
        Assert.Equal(RunStatus.Running, run.Status); Assert.Null(run.NextRunnable());
        Assert.Equal(OrchestrationError.Conflict, Assert.Throws<OrchestrationException>(() => run.Start(1)).Error);
        run.Succeed(0); run.Start(1); run.Succeed(1); run.Start(2); run.Succeed(2);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.All(run.Steps, s => Assert.Equal(1, s.Attempts));
        Assert.Null(run.NextRunnable());
    }

    [Fact]
    public void FailureCancelsLaterStepsAndIsTerminal()
    {
        var run = New(); run.Start(0); run.Succeed(0); run.Start(1);
        run.Fail(1, "invalid_output");
        Assert.Equal(RunStatus.Failed, run.Status); Assert.Equal("invalid_output", run.FailureCode);
        Assert.Equal([StepStatus.Succeeded, StepStatus.Failed, StepStatus.Cancelled], run.Steps.Select(s => s.Status));
        Assert.Null(run.NextRunnable());
        Assert.Throws<OrchestrationException>(() => run.Succeed(1));
        Assert.Throws<OrchestrationException>(() => run.Start(2));
    }

    [Fact]
    public void AbandonedModelStepRunsAgainOnceThenFails()
    {
        var run = New(); run.Start(0);
        run.Abandon(0);
        Assert.Equal(StepStatus.Pending, run.Steps[0].Status); Assert.Equal(1, run.Steps[0].Attempts);
        Assert.Equal(RunStatus.Running, run.Status);
        run.Start(0); Assert.Equal(2, run.Steps[0].Attempts);
        run.Abandon(0);
        Assert.Equal(RunStatus.Failed, run.Status); Assert.Equal("step_attempts_exhausted", run.FailureCode);
    }

    [Fact]
    public void PublicationAllowsThreeAttempts()
    {
        var run = New(); run.Start(0); run.Succeed(0); run.Start(1); run.Succeed(1);
        for (var i = 0; i < 2; i++) { run.Start(2); run.Abandon(2); }
        Assert.Equal(StepStatus.Pending, run.Steps[2].Status);
        run.Start(2); run.Abandon(2);
        Assert.Equal(RunStatus.Failed, run.Status); Assert.Equal(3, run.Steps[2].Attempts);
    }

    [Fact]
    public void RestoreRejectsInconsistentStateAndUnknownVersions()
    {
        var id = Guid.NewGuid();
        WorkflowStep S(int o, StepStatus s, int a = 1) => new(o, (StepKind)o, s, a);
        Assert.Throws<OrchestrationException>(() => WorkflowRun.Restore(id, RunStatus.Running, null,
            [S(0, StepStatus.Running), S(1, StepStatus.Running), S(2, StepStatus.Pending, 0)]));
        Assert.Throws<OrchestrationException>(() => WorkflowRun.Restore(id, RunStatus.Running, null,
            [S(0, StepStatus.Pending, 0), S(1, StepStatus.Succeeded), S(2, StepStatus.Pending, 0)]));
        Assert.Throws<OrchestrationException>(() => WorkflowRun.Restore(id, RunStatus.Succeeded, null,
            [S(0, StepStatus.Succeeded), S(1, StepStatus.Succeeded), S(2, StepStatus.Pending, 0)]));
        Assert.Throws<OrchestrationException>(() => WorkflowRun.Restore(id, RunStatus.Failed, null,
            [S(0, StepStatus.Failed), S(1, StepStatus.Cancelled, 0), S(2, StepStatus.Cancelled, 0)]));
        Assert.Throws<OrchestrationException>(() => WorkflowRun.Restore(id, RunStatus.Queued, null,
            new[] { S(0, StepStatus.Pending, 0), S(1, StepStatus.Pending, 0), S(2, StepStatus.Pending, 0) }.Select(s => s with { Kind = StepKind.Publication })));
        Assert.Equal(OrchestrationError.InvalidCommand,
            Assert.Throws<OrchestrationException>(() => WorkflowRun.Create(id, "analysis-architecture-v2")).Error);
        var restored = WorkflowRun.Restore(id, RunStatus.Running, null, [S(0, StepStatus.Succeeded), S(1, StepStatus.Running), S(2, StepStatus.Pending, 0)]);
        Assert.Equal(1, restored.Running!.Ordinal);
    }
}
