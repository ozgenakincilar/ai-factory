using System.Text.Json;
using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Orchestration.Durable;
using Factory.Tests.Reviews;

namespace Factory.Tests.Orchestration;

public class DurableWorkflowProcessorTests
{
    [Fact]
    public async Task IdleWhenNothingIsClaimable()
    {
        var queue = new Queue(); var provider = new Provider();
        Assert.Equal(StepOutcome.Idle, await new DurableWorkflowProcessor(queue, provider).ProcessNextAsync("w", default));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task AnalysisCheckpointsValidatedResultAndClearsLeaseToken()
    {
        var queue = new Queue { Next = Step(StepKind.Analysis) }; var provider = new Provider();
        Assert.Equal(StepOutcome.Completed, await new DurableWorkflowProcessor(queue, provider).ProcessNextAsync("w", default));
        Assert.Equal(1, provider.Calls); Assert.NotNull(queue.Analysis); Assert.Null(queue.Failure);
        Assert.All(queue.Token!, b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public async Task ArchitectureUsesCheckpointedAnalysisWithoutRerunningAnalyst()
    {
        var queue = new Queue { Next = Step(StepKind.Architecture) }; var provider = new Provider();
        Assert.Equal(StepOutcome.Completed, await new DurableWorkflowProcessor(queue, provider).ProcessNextAsync("w", default));
        Assert.Equal(["architecture_proposal"], provider.Schemas); Assert.NotNull(queue.Architecture);
    }

    [Theory]
    [InlineData("not json", "invalid_output")]
    [InlineData("{}", "invalid_output")]
    public async Task InvalidModelOutputFailsWithoutRetryOrCheckpoint(string content, string code)
    {
        var queue = new Queue { Next = Step(StepKind.Analysis) }; var provider = new Provider { Content = content };
        Assert.Equal(StepOutcome.Failed, await new DurableWorkflowProcessor(queue, provider).ProcessNextAsync("w", default));
        Assert.Equal(code, queue.Failure); Assert.Equal(1, provider.Calls); Assert.Null(queue.Analysis);
    }

    [Fact]
    public async Task ArchitectureWithoutRequirementsFailsBeforeProviderCall()
    {
        var empty = ReviewSamples.Result.Analysis! with { FunctionalRequirements = [] };
        var queue = new Queue { Next = Step(StepKind.Architecture) with { Analysis = empty } }; var provider = new Provider();
        Assert.Equal(StepOutcome.Failed, await new DurableWorkflowProcessor(queue, provider).ProcessNextAsync("w", default));
        Assert.Equal("no_requirements_for_architecture", queue.Failure); Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ProviderExceptionRecordsStaticFailureCode()
    {
        var queue = new Queue { Next = Step(StepKind.Analysis) }; var provider = new Provider { Throw = true };
        Assert.Equal(StepOutcome.Failed, await new DurableWorkflowProcessor(queue, provider).ProcessNextAsync("w", default));
        Assert.Equal("generation_failed", queue.Failure);
    }

    [Theory]
    [InlineData(OrchestrationError.Forbidden, "publication_forbidden", StepOutcome.Failed)]
    [InlineData(OrchestrationError.Integrity, "publication_integrity", StepOutcome.Failed)]
    [InlineData(OrchestrationError.OutcomeUnknown, null, StepOutcome.Released)]
    [InlineData(OrchestrationError.LeaseLost, null, StepOutcome.Released)]
    public async Task PublicationFailuresAreClassifiedAndUnknownOutcomesAreNotRewritten(OrchestrationError error, string? code, StepOutcome expected)
    {
        var queue = new Queue { Next = Step(StepKind.Publication), PublishError = error }; var provider = new Provider();
        Assert.Equal(expected, await new DurableWorkflowProcessor(queue, provider).ProcessNextAsync("w", default));
        Assert.Equal(code, queue.Failure); Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ShutdownReleasesStepWithoutRecordingFailure()
    {
        using var stop = new CancellationTokenSource();
        var queue = new Queue { Next = Step(StepKind.Analysis) }; var provider = new Provider { OnCall = stop.Cancel };
        Assert.Equal(StepOutcome.Released, await new DurableWorkflowProcessor(queue, provider).ProcessNextAsync("w", stop.Token));
        Assert.Null(queue.Failure); Assert.Null(queue.Analysis);
    }

    [Fact]
    public async Task HeartbeatRenewsLongStepsAndLostLeaseStopsExecution()
    {
        var release = new TaskCompletionSource();
        var queue = new Queue { Next = Step(StepKind.Analysis), LoseLeaseAfterRenewals = 2 }; var provider = new Provider { Wait = release.Task };
        var processor = new DurableWorkflowProcessor(queue, provider) { Heartbeat = TimeSpan.FromMilliseconds(20) };
        var outcome = await processor.ProcessNextAsync("w", default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(queue.Renewals >= 2); Assert.Equal(StepOutcome.Released, outcome); Assert.Null(queue.Analysis);
    }

    [Fact]
    public async Task StepTimeoutIsRecordedAsGenerationTimeout()
    {
        var queue = new Queue { Next = Step(StepKind.Analysis) }; var provider = new Provider { Wait = new TaskCompletionSource().Task };
        var processor = new DurableWorkflowProcessor(queue, provider) { StepTimeout = TimeSpan.FromMilliseconds(50) };
        Assert.Equal(StepOutcome.Failed, await processor.ProcessNextAsync("w", default).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("generation_timeout", queue.Failure);
    }

    [Fact]
    public async Task ClaimNotMatchingTheRunsRunningStepIsRejected()
    {
        var step = Step(StepKind.Architecture);
        var queue = new Queue { Next = step with { Steps = [.. step.Steps.Select(s => s with { Status = s.Ordinal == 0 ? StepStatus.Running : StepStatus.Pending })] } };
        await Assert.ThrowsAsync<OrchestrationException>(() => new DurableWorkflowProcessor(queue, new Provider()).ProcessNextAsync("w", default));
    }

    internal static ClaimedStep Step(StepKind kind)
    {
        var ordinal = (int)kind;
        var steps = Enumerable.Range(0, 3).Select(i => new WorkflowStep(i, (StepKind)i,
            i < ordinal ? StepStatus.Succeeded : i == ordinal ? StepStatus.Running : StepStatus.Pending, i <= ordinal ? 1 : 0)).ToArray();
        return new(Guid.NewGuid(), Guid.NewGuid(), ordinal, kind, Guid.NewGuid(), ReviewSamples.Request, steps,
            kind == StepKind.Analysis ? null : ReviewSamples.Result.Analysis, kind == StepKind.Publication ? ReviewSamples.Result.Architecture : null);
    }

    [Fact]
    public async Task EveryProviderCallIsMeteredWithReportedUsage()
    {
        var queue = new Queue { Next = Step(StepKind.Analysis) };
        var provider = new Provider { Usage = new ModelUsage("openai", "gpt-test-2026-01-01", 1200, 340) };
        await new DurableWorkflowProcessor(queue, provider).ProcessNextAsync("w", default);
        var call = Assert.Single(queue.Calls);
        Assert.Equal(("openai", "gpt-test-2026-01-01", 1200, 340, ModelCallOutcome.Completed),
            (call.Provider, call.Model, call.InputTokens, call.OutputTokens, call.Outcome));
    }

    [Theory]
    [InlineData(true, false, ModelCallOutcome.Failed)]
    [InlineData(false, true, ModelCallOutcome.Cancelled)]
    public async Task FailedOrCancelledCallsAreMeteredWithUnknownTokens(bool fail, bool timeout, ModelCallOutcome expected)
    {
        var queue = new Queue { Next = Step(StepKind.Analysis) };
        var provider = new Provider { Throw = fail, Wait = timeout ? new TaskCompletionSource().Task : null };
        var processor = new DurableWorkflowProcessor(queue, provider) { StepTimeout = TimeSpan.FromMilliseconds(50) };
        await processor.ProcessNextAsync("w", default).WaitAsync(TimeSpan.FromSeconds(10));
        var call = Assert.Single(queue.Calls);
        Assert.Equal((expected, (int?)null, (string?)null), (call.Outcome, call.InputTokens, call.Provider));
    }

    [Fact]
    public async Task RefusalsAreMeteredAndUsageWriteFailuresDoNotChangeTheStepOutcome()
    {
        var refused = new Queue { Next = Step(StepKind.Analysis) };
        await new DurableWorkflowProcessor(refused, new Provider { Status = ModelResponseStatus.Refused, Usage = new("openai", "m", 10, 0) })
            .ProcessNextAsync("w", default);
        Assert.Equal((ModelCallOutcome.Refused, 10), (refused.Calls.Single().Outcome, refused.Calls.Single().InputTokens));
        var unrecorded = new Queue { Next = Step(StepKind.Analysis), RecordFails = true };
        Assert.Equal(StepOutcome.Completed, await new DurableWorkflowProcessor(unrecorded, new Provider()).ProcessNextAsync("w", default));
        Assert.NotNull(unrecorded.Analysis);
    }

    private sealed class Provider : IModelProvider
    {
        internal int Calls; internal string? Content; internal bool Throw; internal Action? OnCall; internal Task? Wait;
        internal ModelUsage? Usage; internal ModelResponseStatus Status = ModelResponseStatus.Completed;
        internal List<string> Schemas { get; } = [];
        public async Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct = default)
        {
            Calls++; Schemas.Add(request.OutputSchema!.Name); OnCall?.Invoke();
            if (Wait is not null) await Wait.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (Throw) throw new InvalidOperationException("synthetic provider failure");
            if (Status != ModelResponseStatus.Completed) return new(string.Empty, "test", Status, Usage);
            return new(Content ?? (request.OutputSchema.Name == "analysis_result"
                ? JsonSerializer.Serialize(ReviewSamples.Result.Analysis, AnalysisJson.Options)
                : JsonSerializer.Serialize(ReviewSamples.Result.Architecture, ArchitectureJson.Options)), "test", Usage: Usage);
        }
    }

    private sealed class Queue : IStepQueue
    {
        internal ClaimedStep? Next; internal byte[]? Token; internal string? Failure; internal OrchestrationError? PublishError;
        internal AnalysisResult? Analysis; internal ArchitectureProposal? Architecture;
        internal int Renewals; internal int? LoseLeaseAfterRenewals;
        public Task<ClaimedStep?> ClaimAsync(string workerId, byte[] leaseToken, CancellationToken ct = default)
        { Token = leaseToken; var next = Next; Next = null; return Task.FromResult(next); }
        public Task RenewAsync(Guid attemptId, byte[] leaseToken, CancellationToken ct = default)
        {
            if (++Renewals >= LoseLeaseAfterRenewals) throw new OrchestrationException(OrchestrationError.LeaseLost);
            return Task.CompletedTask;
        }
        public Task CompleteAnalysisAsync(Guid attemptId, byte[] leaseToken, AnalysisResult analysis, CancellationToken ct = default)
        { Analysis = analysis; return Task.CompletedTask; }
        public Task CompleteArchitectureAsync(Guid attemptId, byte[] leaseToken, ArchitectureProposal architecture, CancellationToken ct = default)
        { Architecture = architecture; return Task.CompletedTask; }
        public Task FailAsync(Guid attemptId, byte[] leaseToken, string failureCode, CancellationToken ct = default)
        {
            // A lost lease makes the recorded failure impossible; the database state decides.
            if (LoseLeaseAfterRenewals is not null && Renewals >= LoseLeaseAfterRenewals) throw new OrchestrationException(OrchestrationError.LeaseLost);
            Failure = failureCode; return Task.CompletedTask;
        }
        internal List<ModelCall> Calls { get; } = []; internal bool RecordFails;
        public Task RecordModelCallAsync(Guid attemptId, byte[] leaseToken, ModelCall call, CancellationToken ct = default)
        {
            if (RecordFails) throw new OrchestrationException(OrchestrationError.OutcomeUnknown);
            Calls.Add(call); return Task.CompletedTask;
        }
        public Task<Guid> RequestDecisionAsync(Guid attemptId, byte[] leaseToken, string reason, IReadOnlyList<string> options, CancellationToken ct = default)
            => throw new NotSupportedException("No M7.2 step requests decisions; M8 model routing will.");
        public Task<RunPublication> PublishAsync(ClaimedStep step, byte[] leaseToken, CancellationToken ct = default)
            => PublishError is { } error ? throw new OrchestrationException(error)
                : Task.FromResult(new RunPublication(Guid.NewGuid(), step.CaseId, 1, new string('a', 64)));
    }
}
