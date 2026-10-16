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

    [Fact]
    public async Task EachCallIsReservedAtItsBoundedWorstCaseBeforeItIsMade()
    {
        var queue = new Queue { Next = Step(StepKind.Analysis) }; var provider = new Provider();
        var model = new ModelIdentity("openai", "gpt-4o-mini");
        Assert.Equal(StepOutcome.Completed, await new DurableWorkflowProcessor(queue, provider, model).ProcessNextAsync("w", default));
        var reservation = Assert.Single(queue.Reservations);
        Assert.Equal((model, DurableWorkflowLimits.MaxOutputTokens), (reservation.Model, reservation.Output));
        Assert.True(reservation.Input > 100);
        Assert.Equal(DurableWorkflowLimits.MaxOutputTokens, provider.MaxOutput);
        Assert.Equal(reservation.Id, Assert.Single(queue.Calls).ReservationId);
    }

    [Theory]
    [InlineData(OrchestrationError.BudgetExceeded, "budget_exceeded")]
    [InlineData(OrchestrationError.PriceUnknown, "model_price_unknown")]
    public async Task BudgetRefusalParksTheStepForADecisionWithoutCallingTheModel(OrchestrationError error, string reason)
    {
        var queue = new Queue { Next = Step(StepKind.Architecture), ReserveError = error }; var provider = new Provider();
        Assert.Equal(StepOutcome.Suspended, await new DurableWorkflowProcessor(queue, provider, new ModelIdentity("openai", "m")).ProcessNextAsync("w", default));
        Assert.Equal(0, provider.Calls); Assert.Empty(queue.Calls); Assert.Null(queue.Failure);
        Assert.Equal(reason, queue.Decision!.Value.Reason);
        Assert.Equal(["continue", "stop"], queue.Decision.Value.Options);
    }

    private sealed class Resolver(Func<Factory.Core.Tenancy.ResolvedModel> resolve) : IModelResolver
    {
        public Task<Factory.Core.Tenancy.ResolvedModel> ResolveAsync(ClaimedStep step, byte[] leaseToken, CancellationToken ct) => Task.FromResult(resolve());
    }

    [Fact]
    public async Task UnusableOrFailingCustomerModelsWaitForTheCustomerInsteadOfFallingBack()
    {
        var unavailable = new Queue { Next = Step(StepKind.Analysis) };
        Assert.Equal(StepOutcome.Suspended, await new DurableWorkflowProcessor(unavailable,
            new Resolver(() => throw new OrchestrationException(OrchestrationError.Unavailable))).ProcessNextAsync("w", default));
        Assert.Equal("customer_model_unavailable", unavailable.Decision!.Value.Reason);
        var failing = new Queue { Next = Step(StepKind.Analysis) }; var customer = new Provider { Throw = true };
        Assert.Equal(StepOutcome.Suspended, await new DurableWorkflowProcessor(failing,
            new Resolver(() => new(customer, null, CustomerHosted: true))).ProcessNextAsync("w", default));
        Assert.Equal(("customer_model_unavailable", (string?)null), (failing.Decision!.Value.Reason, failing.Failure));
        Assert.Equal(1, customer.Calls);
        // The same failure on the Factory's own model is an ordinary step failure.
        var factory = new Queue { Next = Step(StepKind.Analysis) };
        await new DurableWorkflowProcessor(factory, new Resolver(() => new(new Provider { Throw = true }, null, false))).ProcessNextAsync("w", default);
        Assert.Equal("generation_failed", factory.Failure);
    }

    [Fact]
    public void InputEstimateIsConservativeForNonAsciiText()
    {
        var request = new ModelRequest(new string('ş', 1000), new string('ğ', 3000), new ModelOutputSchema("s", new string('x', 1000)));
        Assert.Equal(2600, DurableWorkflowProcessor.EstimateInputTokens(request));
    }

    private sealed class Provider : IModelProvider
    {
        internal int Calls; internal string? Content; internal bool Throw; internal Action? OnCall; internal Task? Wait; internal int? MaxOutput;
        internal ModelUsage? Usage; internal ModelResponseStatus Status = ModelResponseStatus.Completed;
        internal List<string> Schemas { get; } = [];
        public async Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct = default)
        {
            Calls++; Schemas.Add(request.OutputSchema!.Name); OnCall?.Invoke(); MaxOutput = request.MaxOutputTokens;
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
        public Task<Factory.Core.Tenancy.ModelEndpoint?> GetModelEndpointAsync(Guid attemptId, byte[] leaseToken, CancellationToken ct = default)
            => Task.FromResult<Factory.Core.Tenancy.ModelEndpoint?>(null);
        internal OrchestrationError? ReserveError; internal List<(ModelIdentity Model, int Input, int Output, Guid Id)> Reservations { get; } = [];
        internal (string Reason, IReadOnlyList<string> Options)? Decision;
        public Task<Guid> ReserveCostAsync(Guid attemptId, byte[] leaseToken, ModelIdentity model, int estimatedInputTokens, int maxOutputTokens, CancellationToken ct = default)
        {
            if (ReserveError is { } error) throw new OrchestrationException(error);
            var id = Guid.NewGuid(); Reservations.Add((model, estimatedInputTokens, maxOutputTokens, id)); return Task.FromResult(id);
        }
        public Task<Guid> RequestDecisionAsync(Guid attemptId, byte[] leaseToken, string reason, IReadOnlyList<string> options, CancellationToken ct = default)
        { Decision = (reason, options); return Task.FromResult(Guid.NewGuid()); }
        public Task<RunPublication> PublishAsync(ClaimedStep step, byte[] leaseToken, CancellationToken ct = default)
            => PublishError is { } error ? throw new OrchestrationException(error)
                : Task.FromResult(new RunPublication(Guid.NewGuid(), step.CaseId, 1, new string('a', 64)));
    }
}
