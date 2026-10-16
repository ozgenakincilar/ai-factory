using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Core.Tenancy;

namespace Factory.Orchestration.Durable;

public interface IModelResolver
{
    // Throws OrchestrationException(Unavailable) when the run's customer model cannot be used.
    Task<ResolvedModel> ResolveAsync(ClaimedStep step, byte[] leaseToken, CancellationToken ct);
}

// Uses the customer model snapshotted on the run when there is one, otherwise the Factory default.
public sealed class RegistryModelResolver(IStepQueue queue, IModelProvider defaultProvider, ModelIdentity? defaultIdentity,
    IModelProviderFactory? factory) : IModelResolver
{
    public async Task<ResolvedModel> ResolveAsync(ClaimedStep step, byte[] leaseToken, CancellationToken ct)
    {
        var endpoint = await queue.GetModelEndpointAsync(step.AttemptId, leaseToken, ct);
        if (endpoint is null) return new ResolvedModel(defaultProvider, defaultIdentity, false);
        if (factory is null) throw new OrchestrationException(OrchestrationError.Unavailable);
        try { return new ResolvedModel(factory.Create(endpoint), new ModelIdentity(endpoint.ProviderLabel, endpoint.Model), true); }
        catch (Exception ex) when (ex is not OrchestrationException)
        {
            // Undecryptable credential, invalid endpoint or unsupported protocol: no details leave here.
            throw new OrchestrationException(OrchestrationError.Unavailable);
        }
    }
}
