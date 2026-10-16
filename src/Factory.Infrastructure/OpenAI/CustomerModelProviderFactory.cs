using System.ClientModel;
using System.ClientModel.Primitives;
using Factory.Core.Models;
using Factory.Core.Tenancy;
using Factory.Infrastructure.Anthropic;
using Factory.Infrastructure.Security;
using OpenAI.Chat;

namespace Factory.Infrastructure.OpenAI;

// Builds providers for customer-registered endpoints (OpenAI-compatible or Anthropic). The credential
// is decrypted only here, held by the SDK client, and never logged. Every connection goes through the
// endpoint policy; SDK retries are disabled like the Factory's own clients.
public sealed class CustomerModelProviderFactory(ISecretProtector protector, ModelEndpointPolicy policy) : IModelProviderFactory
{
    public IModelProvider Create(ModelEndpoint endpoint)
    {
        var uri = policy.ValidateUrl(endpoint.BaseUrl);
        var apiKey = protector.Unprotect(endpoint.EncryptedApiKey, ModelEndpoint.SecretContext(endpoint.OrganizationId, endpoint.ModelId));
        switch (endpoint.Protocol)
        {
            case ModelProtocol.OpenAiCompatible:
                var options = new global::OpenAI.OpenAIClientOptions
                {
                    Endpoint = uri,
                    RetryPolicy = new ClientRetryPolicy(0),
                    NetworkTimeout = TimeSpan.FromSeconds(90),
                    Transport = new HttpClientPipelineTransport(policy.CreateClient())
                };
                return new OpenAiModelProvider(new ChatClient(endpoint.Model, new ApiKeyCredential(apiKey), options), endpoint.Model, endpoint.ProviderLabel);
            case ModelProtocol.Anthropic:
                return new AnthropicModelProvider(AnthropicModelProvider.CreateClient(apiKey, uri, policy.CreateClient()), endpoint.Model, endpoint.ProviderLabel);
            default:
                throw new NotSupportedException("unsupported_model_protocol");
        }
    }
}
