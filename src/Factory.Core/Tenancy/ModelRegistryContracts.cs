using Factory.Core.Authorization;
using Factory.Core.Models;
using Factory.Core.Orchestration;

namespace Factory.Core.Tenancy;

// Customer-provided models (M8.4). The Factory never restricts which model a customer registers;
// it measures capabilities and makes the customer decide explicitly when a model is unverified.
public enum ModelProtocol { OpenAiCompatible, Anthropic }
public enum ModelVerificationStatus { Unverified, Verified, Failed }

// ApiKey is accepted once at registration, encrypted immediately and never returned.
public sealed record ModelRegistration(string Name, ModelProtocol Protocol, string BaseUrl, string Model, string ApiKey,
    long? InputMicrosPerMillion = null, long? OutputMicrosPerMillion = null);

// Safe projection: no credential material.
public sealed record RegisteredModel(Guid Id, string OrganizationId, string Name, ModelProtocol Protocol, string BaseUrl, string Model,
    ModelVerificationStatus Status, bool? StructuredOutput, DateTimeOffset? VerifiedAt, DateTimeOffset CreatedAt);

public sealed record ProjectModelAssignment(string ProjectId, Guid? ModelId, bool AcceptedCapabilityRisk, DateTimeOffset AssignedAt);

// Resolved endpoint with its encrypted credential; decrypted only inside the provider factory.
public sealed record ModelEndpoint(Guid ModelId, string OrganizationId, ModelProtocol Protocol, string BaseUrl, string Model, byte[] EncryptedApiKey)
{
    public string ProviderLabel => "customer:" + ModelId.ToString("N");
    public static string SecretContext(string organizationId, Guid modelId) => $"model-api-key:{organizationId}:{modelId:N}";
}

public static class ModelRegistryLimits
{
    public const int MaxApiKeyLength = 4096;
    public const int VerificationMaxOutputTokens = 50;
}

public interface IModelRegistryStore
{
    // Organization Owner/Admin only. encryptedApiKey must be bound to SecretContext(org, id).
    Task<RegisteredModel> RegisterAsync(TrustedIdentity actor, string organizationId, Guid id, ModelRegistration registration, byte[] encryptedApiKey,
        CancellationToken ct = default);
    Task<IReadOnlyList<RegisteredModel>> ListAsync(TrustedIdentity actor, string organizationId, CancellationToken ct = default);
    Task<ModelEndpoint> GetEndpointAsync(TrustedIdentity actor, string organizationId, Guid modelId, CancellationToken ct = default);
    Task<RegisteredModel> RecordVerificationAsync(TrustedIdentity actor, string organizationId, Guid modelId, ModelVerificationStatus status,
        bool? structuredOutput, CancellationToken ct = default);
    // Null modelId returns the project to the Factory default. Unverified or non-structured models
    // require acceptCapabilityRisk, otherwise CapabilityUnverified.
    Task<ProjectModelAssignment> AssignAsync(TrustedIdentity actor, string projectId, Guid? modelId, bool acceptCapabilityRisk,
        CancellationToken ct = default);
}

public interface ISecretProtector
{
    byte[] Protect(string secret, string context);
    string Unprotect(byte[] protectedSecret, string context);
}

public interface IModelProviderFactory
{
    IModelProvider Create(ModelEndpoint endpoint);
}

// Worker-side: which provider and reservation identity a claimed step uses.
public sealed record ResolvedModel(IModelProvider Provider, ModelIdentity? Identity, bool CustomerHosted,
    ModelRoute? Route = null);
