using System.Security.Cryptography;
using Factory.Core.Agents;
using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Orchestration.Agents;

namespace Factory.Orchestration.Durable;

public enum StepOutcome { Idle, Completed, Failed, Released, Suspended }

// Application service: leases one step, executes it outside database transactions and records
// its checkpoint. Business transitions belong to WorkflowRun and the database guards.
// No automatic retry of model failures; a lost lease lets another worker repeat only the
// unfinished step, which can repeat its provider call (not exactly-once).
// With a model identity, every call is reserved against budgets before it is made; an exceeded
// budget or an unpriced model under a budget parks the step for a human decision.
// The resolver picks the run's model: the Factory default, or the customer model snapshotted on the
// run. A customer model that cannot be reached is never silently replaced by a Factory model.
public sealed class DurableWorkflowProcessor
{
    internal static readonly string[] BudgetOptions = ["continue", "stop"];
    private readonly IStepQueue queue;
    private readonly IModelResolver resolver;

    public DurableWorkflowProcessor(IStepQueue queue, IModelResolver resolver) { this.queue = queue; this.resolver = resolver; }

    public DurableWorkflowProcessor(IStepQueue queue, IModelProvider provider, ModelIdentity? model = null)
        : this(queue, new RegistryModelResolver(queue, provider, model, null)) { }

    internal TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(DurableWorkflowLimits.HeartbeatSeconds);
    internal TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(DurableWorkflowLimits.StepSeconds);

    public async Task<StepOutcome> ProcessNextAsync(string workerId, CancellationToken stopping)
    {
        var token = RandomNumberGenerator.GetBytes(32);
        try
        {
            var step = await queue.ClaimAsync(workerId, token, stopping);
            if (step is null) return StepOutcome.Idle;
            // Defense in depth: the claimed step must be the run's only Running step.
            if (WorkflowRun.Restore(step.RunId, RunStatus.Running, null, step.Steps).Running?.Ordinal != step.Ordinal)
                throw new OrchestrationException(OrchestrationError.Integrity);
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            execution.CancelAfter(StepTimeout);
            using var renewal = new CancellationTokenSource();
            var heartbeat = RenewAsync(step.AttemptId, token, execution, renewal.Token);
            try { return await ExecuteAsync(step, token, execution, stopping); }
            finally { renewal.Cancel(); await heartbeat; }
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    private async Task<StepOutcome> ExecuteAsync(ClaimedStep step, byte[] token, CancellationTokenSource execution, CancellationToken stopping)
    {
        Factory.Core.Tenancy.ResolvedModel? resolved = null;
        try
        {
            if (step.Kind != StepKind.Publication) resolved = await resolver.ResolveAsync(step, token, CancellationToken.None);
            // Every provider call of this attempt is reserved (when a model identity is known)
            // and metered, whatever its outcome.
            var identity = resolved?.Identity;
            var metered = resolved is null ? null : new MeteredProvider(resolved.Provider, call => Record(step.AttemptId, token, call),
                identity is null ? null : (input, output) => queue.ReserveCostAsync(step.AttemptId, token, identity, input, output, CancellationToken.None));
            var request = new AgentRequest(step.Request.ProjectId, step.Request.Task, step.Request.Context,
                step.Request.ConfirmedRequirements?.ToArray());
            switch (step.Kind)
            {
                case StepKind.Analysis:
                    var analysis = await new AnalystAgent(metered!).ExecuteAsync(request, execution.Token);
                    if (!analysis.Success) return await Fail(Code(analysis.FailureKind));
                    await queue.CompleteAnalysisAsync(step.AttemptId, token, analysis.Analysis!, CancellationToken.None);
                    return StepOutcome.Completed;
                case StepKind.Architecture:
                    if (step.Analysis is null) throw new OrchestrationException(OrchestrationError.Integrity);
                    if (step.Analysis.FunctionalRequirements.Count == 0) return await Fail("no_requirements_for_architecture");
                    var proposal = await new ArchitectAgent(metered!).ExecuteAsync(request with { Analysis = step.Analysis }, execution.Token);
                    if (!proposal.Success) return await Fail(Code(proposal.FailureKind));
                    await queue.CompleteArchitectureAsync(step.AttemptId, token, proposal.Architecture!, CancellationToken.None);
                    return StepOutcome.Completed;
                default:
                    try { await queue.PublishAsync(step, token, CancellationToken.None); return StepOutcome.Completed; }
                    catch (OrchestrationException ex) when (ex.Error is OrchestrationError.Forbidden or OrchestrationError.Integrity
                        or OrchestrationError.StaleRevision)
                    {
                        // Stale: the work item or case changed after submission (e.g. a human decision);
                        // never overwrite it with an older baseline.
                        return await Fail(ex.Error switch
                        {
                            OrchestrationError.Forbidden => "publication_forbidden",
                            OrchestrationError.StaleRevision => "publication_stale",
                            _ => "publication_integrity"
                        });
                    }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Shutdown: leave the lease to expire so another worker can resume this step.
            return StepOutcome.Released;
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            return await Fail("generation_timeout");
        }
        catch (OrchestrationException ex) when (ex.Error is OrchestrationError.BudgetExceeded or OrchestrationError.PriceUnknown
            or OrchestrationError.Unavailable)
        {
            // No provider call was made. The customer decides; nothing continues automatically.
            return await Suspend(ex.Error switch
            {
                OrchestrationError.BudgetExceeded => "budget_exceeded",
                OrchestrationError.PriceUnknown => "model_price_unknown",
                _ => "customer_model_unavailable"
            });
        }
        catch (OrchestrationException ex) when (ex.Error is OrchestrationError.LeaseLost or OrchestrationError.OutcomeUnknown
            or OrchestrationError.TransactionAborted)
        {
            // Not rewritten as failure: the database state decides; an unexpired lease is reclaimed later.
            return StepOutcome.Released;
        }
        catch (Exception ex) when (ex is not OrchestrationException)
        {
            // A failing customer endpoint is the customer's to fix or stop; never a silent fallback.
            if (resolved?.CustomerHosted == true) return await Suspend("customer_model_unavailable");
            // Provider/SDK failures: static code only, no message or payload.
            return await Fail("generation_failed");
        }

        async Task<StepOutcome> Suspend(string reason)
        {
            try { await queue.RequestDecisionAsync(step.AttemptId, token, reason, BudgetOptions, CancellationToken.None); return StepOutcome.Suspended; }
            catch (OrchestrationException wait) when (wait.Error is OrchestrationError.LeaseLost or OrchestrationError.OutcomeUnknown
                or OrchestrationError.TransactionAborted)
            { return StepOutcome.Released; }
        }

        async Task<StepOutcome> Fail(string code)
        {
            try { await queue.FailAsync(step.AttemptId, token, code, CancellationToken.None); return StepOutcome.Failed; }
            catch (OrchestrationException ex) when (ex.Error is OrchestrationError.LeaseLost or OrchestrationError.OutcomeUnknown
                or OrchestrationError.TransactionAborted)
            { return StepOutcome.Released; }
        }
    }

    private async Task RenewAsync(Guid attempt, byte[] token, CancellationTokenSource execution, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(Heartbeat, stop);
                try { await queue.RenewAsync(attempt, token, stop); }
                catch (OrchestrationException ex) when (ex.Error == OrchestrationError.LeaseLost)
                {
                    // Another worker may own the step now; stop spending on this attempt.
                    execution.Cancel(); return;
                }
                catch (OrchestrationException) { }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    // A failed usage write must not change the step outcome; it is an undercount, documented as such.
    private async Task Record(Guid attempt, byte[] token, ModelCall call)
    {
        try { await queue.RecordModelCallAsync(attempt, token, call, CancellationToken.None); }
        catch (OrchestrationException) { }
    }

    // Conservative input estimate for reservations: about two characters per token plus overhead,
    // so non-English text and schema/prompt framing are not under-reserved.
    internal static int EstimateInputTokens(ModelRequest request)
        => (int)Math.Min(2_000_000L, (request.SystemPrompt.Length + request.UserPrompt.Length + (request.OutputSchema?.JsonSchema.Length ?? 0)) / 2L + 100);

    private sealed class MeteredProvider(IModelProvider inner, Func<ModelCall, Task> record, Func<int, int, Task<Guid>>? reserve) : IModelProvider
    {
        public async Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            // Every call is bounded so its worst-case cost can be reserved before it is made.
            request = request with { MaxOutputTokens = Math.Min(request.MaxOutputTokens ?? DurableWorkflowLimits.MaxOutputTokens, DurableWorkflowLimits.MaxOutputTokens) };
            Guid? reservation = reserve is null ? null : await reserve(EstimateInputTokens(request), request.MaxOutputTokens!.Value);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int Elapsed() => (int)Math.Min(clock.ElapsedMilliseconds, 86_400_000);
            ModelResponse response;
            try { response = await inner.GenerateAsync(request, cancellationToken); }
            catch (OperationCanceledException)
            {
                await record(new ModelCall(null, null, null, null, Elapsed(), ModelCallOutcome.Cancelled, reservation)); throw;
            }
            catch
            {
                await record(new ModelCall(null, null, null, null, Elapsed(), ModelCallOutcome.Failed, reservation)); throw;
            }
            var usage = response.Usage;
            await record(new ModelCall(usage?.Provider, usage?.Model ?? response.Model, usage?.InputTokens, usage?.OutputTokens, Elapsed(),
                response.Status switch
                {
                    ModelResponseStatus.Refused => ModelCallOutcome.Refused,
                    ModelResponseStatus.Incomplete => ModelCallOutcome.Incomplete,
                    ModelResponseStatus.Empty => ModelCallOutcome.Empty,
                    _ => ModelCallOutcome.Completed
                }, reservation));
            return response;
        }
    }

    private static string Code(AgentFailureKind? kind) => kind switch
    {
        AgentFailureKind.InvalidRequest => "invalid_request",
        AgentFailureKind.Refused => "refused",
        AgentFailureKind.IncompleteResponse => "incomplete_response",
        AgentFailureKind.EmptyResponse => "empty_response",
        _ => "invalid_output"
    };
}
