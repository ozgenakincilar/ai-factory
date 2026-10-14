using System.Text.Json;
using Factory.Core.Architecture;
using Factory.Core.Authorization;
using Factory.Core.Lifecycle;
using Factory.Core.Models;
using Factory.Core.Reviews;
using Factory.Core.Workflows;
using Factory.Orchestration.Lifecycle;
using Factory.Tests.Reviews;

namespace Factory.Tests.Lifecycle;

public class LifecycleServiceTests
{
    private static readonly TrustedIdentity Actor = new("https://test.invalid", "generator", PrincipalKind.Human);
    private static GenerationCommand Command => new(Guid.NewGuid(), null, null, LifecycleLimits.WorkflowVersion, ReviewSamples.Request);

    [Fact]
    public async Task ReplayDoesNotExecuteAgentsOrAttemptPublication()
    {
        var store = new Store { Replay = true }; var provider = new Provider();
        using var service = new LifecycleService(store, store, provider);
        var response = await service.ExecuteAsync(Actor, Command, default);
        Assert.True(response.Replayed); Assert.Equal(0, provider.Calls); Assert.Equal(0, store.Publications);
    }

    [Fact]
    public async Task AdmissionFailureDoesNotExecuteAgents()
    {
        var store = new Store { AdmissionFailure = true }; var provider = new Provider();
        using var service = new LifecycleService(store, store, provider);
        Assert.Equal(LifecycleError.OutcomeUnknown, (await Assert.ThrowsAsync<LifecycleException>(() => service.ExecuteAsync(Actor, Command, default))).Error);
        Assert.Equal(0, provider.Calls); Assert.Empty(store.Finishes);
    }

    [Fact]
    public async Task ExpiredAdmissionNeverCallsProviderAndRecordsTimeout()
    {
        var store = new Store { Deadline = DateTimeOffset.UtcNow.AddSeconds(-1) }; var provider = new Provider();
        using var service = new LifecycleService(store, store, provider);
        var result = await service.ExecuteAsync(Actor, Command, default);
        Assert.Equal(GenerationStatus.GenerationFailed, result.Outcome.Status);
        Assert.Equal("generation_timeout", result.Outcome.FailureCode); Assert.Equal(0, provider.Calls); Assert.Equal(0, store.Publications);
    }

    [Fact]
    public async Task SuccessfulRunUsesAdmittedIdentityAndExactlyTwoProviderCalls()
    {
        var store = new Store(); var provider = new Provider();
        using var service = new LifecycleService(store, store, provider);
        var result = await service.ExecuteAsync(Actor, Command, default);
        Assert.Equal(GenerationStatus.Published, result.Outcome.Status); Assert.Equal(2, provider.Calls);
        Assert.Equal(store.Run, store.PublishedWorkflowId); Assert.Single(store.PublishedTokens);
        Assert.All(store.PublishedTokens.Single(), b => Assert.Equal((byte)0, b)); // ownership buffer cleared on exit
        Assert.Empty(store.Finishes);
    }

    [Fact]
    public async Task UnknownPublicationCommitIsNotRecordedAsFailureOrRetried()
    {
        var store = new Store { PublicationFailure = LifecycleError.OutcomeUnknown }; var provider = new Provider();
        using var service = new LifecycleService(store, store, provider);
        var error = await Assert.ThrowsAsync<LifecycleException>(() => service.ExecuteAsync(Actor, Command, default));
        Assert.Equal(LifecycleError.OutcomeUnknown, error.Error); Assert.Equal(store.Run, error.RunId);
        Assert.Equal(2, provider.Calls); Assert.Equal(1, store.Publications); Assert.Empty(store.Finishes);
    }

    [Theory]
    [InlineData(LifecycleError.StaleRevision, "publication_stale")]
    [InlineData(LifecycleError.Integrity, "publication_integrity")]
    [InlineData(LifecycleError.TransactionAborted, "publication_failed")]
    public async Task DefinitePublicationFailureRecordsHistoricalFailure(LifecycleError error, string code)
    {
        var store = new Store { PublicationFailure = error }; var provider = new Provider();
        using var service = new LifecycleService(store, store, provider);
        var response = await service.ExecuteAsync(Actor, Command, default);
        Assert.Equal(GenerationStatus.PublicationFailed, response.Outcome.Status); Assert.Equal(code, response.Outcome.FailureCode);
        Assert.Equal(2, provider.Calls); Assert.Single(store.Finishes);
    }

    [Fact]
    public async Task CancellationImmediatelyAfterAdmissionRecordsTerminalOutcome()
    {
        using var cancel = new CancellationTokenSource(); var store = new Store { OnAdmission = cancel.Cancel }; var provider = new Provider();
        using var service = new LifecycleService(store, store, provider);
        var response = await service.ExecuteAsync(Actor, Command, cancel.Token);
        Assert.Equal(GenerationStatus.Cancelled, response.Outcome.Status); Assert.Equal(0, provider.Calls); Assert.Single(store.Finishes);
    }

    [Fact]
    public async Task CancellationIsRecordedWithoutPartialArtifact()
    {
        using var cancel = new CancellationTokenSource(); var store = new Store(); var provider = new Provider { Cancel = cancel };
        using var service = new LifecycleService(store, store, provider);
        var response = await service.ExecuteAsync(Actor, Command, cancel.Token);
        Assert.Equal(GenerationStatus.Cancelled, response.Outcome.Status); Assert.Equal(0, store.Publications); Assert.Single(store.Finishes);
    }

    private sealed class Provider : IModelProvider
    {
        internal int Calls; internal CancellationTokenSource? Cancel;
        public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct = default)
        {
            Calls++; Cancel?.Cancel(); ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ModelResponse(Calls == 1 ? JsonSerializer.Serialize(ReviewSamples.Result.Analysis, ArchitectureJson.Options)
                : JsonSerializer.Serialize(ReviewSamples.Result.Architecture, ArchitectureJson.Options), "test"));
        }
    }
    private sealed class Store : ILifecycleStore, IReviewStore
    {
        internal readonly Guid Run = Guid.NewGuid(); private readonly Guid _work = Guid.NewGuid();
        internal bool Replay, AdmissionFailure; internal LifecycleError? PublicationFailure;
        internal Action? OnAdmission;
        internal DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddSeconds(LifecycleLimits.ExecutionSeconds);
        internal int Publications; internal Guid PublishedWorkflowId;
        internal List<GenerationStatus> Finishes { get; } = []; internal List<byte[]> PublishedTokens { get; } = [];
        private GenerationOutcome Outcome => new(_work, Run, GenerationStatus.Running, 202, null, null);
        public Task<GenerationAdmission> AdmitAsync(TrustedIdentity actor, GenerationCommand command, byte[] token, CancellationToken ct = default)
        {
            if (AdmissionFailure) throw new LifecycleException(LifecycleError.OutcomeUnknown);
            OnAdmission?.Invoke();
            return Task.FromResult(new GenerationAdmission(Outcome, Replay, Guid.NewGuid(), 1, 0, Deadline));
        }
        public Task<GenerationOutcome> PublishManagedAsync(TrustedIdentity actor, GenerationCommand command, GenerationAdmission admission, byte[] token, WorkflowResult result, CancellationToken ct = default)
            => PublishAsync(actor, command, admission, token, result, ct);
        public Task<GenerationOutcome> PublishAsync(TrustedIdentity actor, GenerationCommand command, GenerationAdmission admission, byte[] token, WorkflowResult result, CancellationToken ct = default)
        {
            Publications++; PublishedWorkflowId = result.WorkflowId; PublishedTokens.Add(token);
            if (PublicationFailure is { } error) throw new LifecycleException(error);
            return Task.FromResult(Outcome with { Status = GenerationStatus.Published, HttpStatus = 201 });
        }
        public Task<GenerationOutcome> FinishAsync(TrustedIdentity actor, string project, Guid run, byte[] token, GenerationStatus status, string failure, CancellationToken ct = default)
        { Finishes.Add(status); return Task.FromResult(Outcome with { Status = status, FailureCode = failure }); }
        public Task<WorkItemSnapshot> GetWorkItemAsync(TrustedIdentity actor, string project, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GenerationOutcome> GetRunAsync(TrustedIdentity actor, string project, Guid id, CancellationToken ct = default) => Task.FromResult(Outcome);
        public Task<ReviewCaseSnapshot> CreateAsync(WorkflowRequest request, WorkflowResult result, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReviewCaseSnapshot> PublishAsync(Guid id, long revision, WorkflowRequest request, WorkflowResult result, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReviewCaseSnapshot?> GetCaseAsync(string project, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReviewArtifact?> GetArtifactAsync(string project, Guid id, int version, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
