using System.Security.Cryptography;
using Factory.Core.Authorization;
using Factory.Core.Lifecycle;
using Factory.Core.Models;
using Factory.Core.Reviews;
using Factory.Core.Workflows;
using Factory.Orchestration.Agents;
using Factory.Orchestration.Workflows;

namespace Factory.Orchestration.Lifecycle;

// Singleton process bound; database admission also bounds commands across processes.
// No waiting queue, scheduling, resumption, or automatic generation retry.
public sealed class LifecycleService(ILifecycleStore store, IReviewStore reviews, IModelProvider provider) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(LifecycleLimits.ConcurrentAttempts);
    public async Task<GenerationResponse> ExecuteAsync(TrustedIdentity actor, GenerationCommand command, CancellationToken ct)
    {
        command = command with { Request = command.Request with { ConfirmedRequirements = command.Request.ConfirmedRequirements?.ToArray() } };
        GenerationFingerprint.Validate(command);
        var token = RandomNumberGenerator.GetBytes(32);
        var acquired = false;
        Task<WorkflowResult>? execution = null;
        GenerationAdmission? admission = null;
        try
        {
            admission = await store.AdmitAsync(actor, command, token, ct);
            if (admission.Replayed) return new(admission.Outcome, true);
            if (ct.IsCancellationRequested) return await Finish(GenerationStatus.Cancelled, "generation_cancelled");
            acquired = _slots.Wait(0);
            if (!acquired) return await Finish(GenerationStatus.GenerationFailed, "generation_capacity");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var remaining = admission.Deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) deadline.Cancel();
            else deadline.CancelAfter(remaining < TimeSpan.FromSeconds(LifecycleLimits.ExecutionSeconds)
                ? remaining : TimeSpan.FromSeconds(LifecycleLimits.ExecutionSeconds));
            var bounded = new BoundedProvider(provider);
            var workflow = new AnalysisArchitectureWorkflow(new AnalystAgent(bounded), new ArchitectAgent(bounded));
            WorkflowResult result;
            try
            {
                execution = workflow.ExecuteAsync(admission.Outcome.RunId, command.Request, deadline.Token);
                result = await execution.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            { return await Finish(ct.IsCancellationRequested ? GenerationStatus.Cancelled : GenerationStatus.GenerationFailed,
                ct.IsCancellationRequested ? "generation_cancelled" : "generation_timeout"); }
            catch (Exception) { return await Finish(GenerationStatus.GenerationFailed, "generation_failed"); }
            if (deadline.IsCancellationRequested)
                return await Finish(ct.IsCancellationRequested ? GenerationStatus.Cancelled : GenerationStatus.GenerationFailed,
                    ct.IsCancellationRequested ? "generation_cancelled" : "generation_timeout");
            if (!result.Success) return await Finish(GenerationStatus.GenerationFailed, "generation_failed");
            try
            {
                var outcome = await reviews.PublishManagedAsync(actor, command, admission, token, result, deadline.Token);
                return new(outcome, false);
            }
            catch (LifecycleException ex) when (ex.Error is LifecycleError.StaleRevision or LifecycleError.Integrity or LifecycleError.TransactionAborted)
            { return await Finish(GenerationStatus.PublicationFailed, ex.Error switch
                { LifecycleError.StaleRevision => "publication_stale", LifecycleError.Integrity => "publication_integrity", _ => "publication_failed" }); }
            catch (ArgumentException) { return await Finish(GenerationStatus.GenerationFailed, "generation_invalid"); }
            catch (ReviewIntegrityException) { return await Finish(GenerationStatus.GenerationFailed, "generation_invalid"); }
            catch (OperationCanceledException) { throw new LifecycleException(LifecycleError.OutcomeUnknown); }

            async Task<GenerationResponse> Finish(GenerationStatus status, string code)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { return new(await store.FinishAsync(actor, command.Request.ProjectId, admission.Outcome.RunId, token, status, code, cleanup.Token), false); }
                catch (OperationCanceledException) { throw new LifecycleException(LifecycleError.OutcomeUnknown); }
            }
        }
        catch (LifecycleException ex) when (admission is not null)
        { throw new LifecycleException(ex.Error) { RunId = admission.Outcome.RunId, WorkItemId = admission.Outcome.WorkItemId }; }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
            if (acquired)
            {
                if (execution is { IsCompleted: false })
                    _ = execution.ContinueWith(task => { _ = task.Exception; _slots.Release(); }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                else _slots.Release();
            }
        }
    }

    private sealed class BoundedProvider(IModelProvider inner) : IModelProvider
    {
        private int _calls;
        public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) > LifecycleLimits.ProviderCalls) throw new InvalidOperationException("provider_call_limit");
            return inner.GenerateAsync(request, cancellationToken);
        }
    }
    public void Dispose() => _slots.Dispose();
}
