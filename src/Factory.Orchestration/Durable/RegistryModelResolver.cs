using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Core.Tenancy;

namespace Factory.Orchestration.Durable;

public interface IModelResolver
{
    // Throws OrchestrationException(Unavailable) when the run's customer model cannot be used.
    Task<ResolvedModel> ResolveAsync(ClaimedStep step, byte[] leaseToken, CancellationToken ct);
}

// One configured Factory model: the provider and the identity that prices its reservations.
public sealed record CatalogEntry(IModelProvider Provider, ModelIdentity? Identity);

// Operator mapping from tier to Factory model. Tiers without an explicit entry use the default
// model, so a single-model deployment serves every tier with it; routes record the model used.
public sealed class ModelCatalog
{
    private readonly CatalogEntry _default;
    private readonly IReadOnlyDictionary<ModelTier, CatalogEntry> _tiers;

    public ModelCatalog(CatalogEntry defaultEntry, IReadOnlyDictionary<ModelTier, CatalogEntry>? tiers = null)
    {
        _default = defaultEntry;
        _tiers = tiers ?? new Dictionary<ModelTier, CatalogEntry>();
    }

    public CatalogEntry For(ModelTier tier) => _tiers.TryGetValue(tier, out var entry) ? entry : _default;
}

// Routes each model step by ModelRoutingPolicy. The customer model snapshotted on the run, when there
// is one, serves every step (the customer's choice wins; the route records reason customer_model).
public sealed class RegistryModelResolver(IStepQueue queue, ModelCatalog catalog, IModelProviderFactory? factory) : IModelResolver
{
    public RegistryModelResolver(IStepQueue queue, IModelProvider defaultProvider, ModelIdentity? defaultIdentity, IModelProviderFactory? factory)
        : this(queue, new ModelCatalog(new CatalogEntry(defaultProvider, defaultIdentity)), factory) { }

    public async Task<ResolvedModel> ResolveAsync(ClaimedStep step, byte[] leaseToken, CancellationToken ct)
    {
        var route = ModelRoutingPolicy.Route(step.Kind);
        var endpoint = await queue.GetModelEndpointAsync(step.AttemptId, leaseToken, ct);
        if (endpoint is null)
        {
            var entry = catalog.For(route.Tier);
            return new ResolvedModel(entry.Provider, entry.Identity, false, route);
        }
        if (factory is null) throw new OrchestrationException(OrchestrationError.Unavailable);
        try
        {
            return new ResolvedModel(factory.Create(endpoint), new ModelIdentity(endpoint.ProviderLabel, endpoint.Model), true,
                route with { Reason = ModelRoutingPolicy.CustomerModel });
        }
        catch (Exception ex) when (ex is not OrchestrationException)
        {
            // Undecryptable credential, invalid endpoint or unsupported protocol: no details leave here.
            throw new OrchestrationException(OrchestrationError.Unavailable);
        }
    }
}
