using System.ClientModel;
using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Infrastructure.Anthropic;
using Factory.Infrastructure.OpenAI;
using Factory.Infrastructure.Orchestration;
using Factory.Orchestration.Durable;
using Factory.Worker;
using OpenAI.Chat;

// Standard precedence (highest first): command line, environment, Development User Secrets, JSON.
var builder = Host.CreateApplicationBuilder(args);

var connection = builder.Configuration.GetConnectionString("Orchestration");
if (string.IsNullOrWhiteSpace(connection))
    throw new InvalidOperationException("ConnectionStrings:Orchestration is not configured; use the restricted worker role.");

// Factory models. Generation:Provider selects the default ("openai" or "anthropic"); optional
// Generation:Tiers:{Economy|Standard|Strong} = "<provider>:<model>" map routing tiers to models.
// Tiers without a mapping use the default. Only the keys of providers in use are required.
// No SDK retries: a lost or failed call is recorded, never silently repeated by the client.
var entries = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
CatalogEntry Entry(string provider, string? model)
{
    var key = provider + ":" + model;
    if (entries.TryGetValue(key, out var existing)) return existing;
    CatalogEntry created;
    switch (provider)
    {
        case "openai":
            var apiKey = builder.Configuration["OpenAI:ApiKey"];
            OpenAiConfiguration.ValidateApiKey(apiKey);
            var openAiModel = model ?? builder.Configuration["OpenAI:Model"] ?? "gpt-4o-mini";
            created = new CatalogEntry(new OpenAiModelProvider(new ChatClient(openAiModel, new ApiKeyCredential(apiKey!),
                new OpenAI.OpenAIClientOptions
                {
                    RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0),
                    NetworkTimeout = TimeSpan.FromSeconds(90)
                }), openAiModel), new ModelIdentity("openai", openAiModel));
            break;
        case "anthropic":
            var anthropicKey = builder.Configuration["Anthropic:ApiKey"];
            AnthropicConfiguration.ValidateApiKey(anthropicKey);
            var claudeModel = model ?? builder.Configuration["Anthropic:Model"] ?? AnthropicConfiguration.DefaultModel;
            created = new CatalogEntry(new AnthropicModelProvider(AnthropicModelProvider.CreateClient(anthropicKey!), claudeModel),
                new ModelIdentity("anthropic", claudeModel));
            break;
        default:
            throw new InvalidOperationException("Factory model providers must be 'openai' or 'anthropic'.");
    }
    return entries[key] = created;
}
var defaultEntry = Entry(builder.Configuration["Generation:Provider"] ?? "openai", null);
var tiers = new Dictionary<ModelTier, CatalogEntry>();
foreach (var tier in Enum.GetValues<ModelTier>())
{
    var spec = builder.Configuration[$"Generation:Tiers:{tier}"];
    if (string.IsNullOrWhiteSpace(spec)) continue;
    var parts = spec.Split(':', 2);
    if (parts.Length != 2 || !System.Text.RegularExpressions.Regex.IsMatch(parts[1], "^[A-Za-z0-9._/-]{1,200}$"))
        throw new InvalidOperationException($"Generation:Tiers:{tier} must be '<provider>:<model>'.");
    tiers[tier] = Entry(parts[0], parts[1]);
}

builder.Services.AddSingleton<IStepQueue>(new PostgresOrchestrationStore(connection));
// Customer models need the master key (Secrets:MasterKey) to decrypt their credential and the
// endpoint egress policy. Without the key, runs assigned to customer models wait for a decision.
var protector = Factory.Infrastructure.Security.AesGcmSecretProtector.FromConfiguration(builder.Configuration["Secrets:MasterKey"]);
var policy = Factory.Infrastructure.Security.ModelEndpointPolicy.FromConfiguration(
    builder.Configuration.GetValue<bool>(Factory.Infrastructure.Security.ModelEndpointPolicy.Setting),
    builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"));
// Each identity prices budget reservations before its calls.
builder.Services.AddSingleton(sp => new DurableWorkflowProcessor(sp.GetRequiredService<IStepQueue>(),
    new RegistryModelResolver(sp.GetRequiredService<IStepQueue>(), new ModelCatalog(defaultEntry, tiers),
        protector is null ? null : new CustomerModelProviderFactory(protector, policy))));
builder.Services.AddHostedService<WorkflowWorkerService>();

builder.Build().Run();
