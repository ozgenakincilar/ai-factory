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

// Factory default provider for projects without a customer model: "openai" (default) or "anthropic".
// Only the selected provider's key is required. No SDK retries: a lost or failed call is recorded,
// never silently repeated by the client.
var providerName = builder.Configuration["Generation:Provider"] ?? "openai";
IModelProvider factoryProvider;
ModelIdentity factoryIdentity;
switch (providerName)
{
    case "openai":
        var apiKey = builder.Configuration["OpenAI:ApiKey"];
        OpenAiConfiguration.ValidateApiKey(apiKey);
        var model = builder.Configuration["OpenAI:Model"] ?? "gpt-4o-mini";
        factoryProvider = new OpenAiModelProvider(new ChatClient(model, new ApiKeyCredential(apiKey!),
            new OpenAI.OpenAIClientOptions
            {
                RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0),
                NetworkTimeout = TimeSpan.FromSeconds(90)
            }), model);
        factoryIdentity = new ModelIdentity("openai", model);
        break;
    case "anthropic":
        var anthropicKey = builder.Configuration["Anthropic:ApiKey"];
        AnthropicConfiguration.ValidateApiKey(anthropicKey);
        var claudeModel = builder.Configuration["Anthropic:Model"] ?? AnthropicConfiguration.DefaultModel;
        factoryProvider = new AnthropicModelProvider(AnthropicModelProvider.CreateClient(anthropicKey!), claudeModel);
        factoryIdentity = new ModelIdentity("anthropic", claudeModel);
        break;
    default:
        throw new InvalidOperationException("Generation:Provider must be 'openai' or 'anthropic'.");
}

builder.Services.AddSingleton<IStepQueue>(new PostgresOrchestrationStore(connection));
builder.Services.AddSingleton(factoryProvider);
// Customer models need the master key (Secrets:MasterKey) to decrypt their credential and the
// endpoint egress policy. Without the key, runs assigned to customer models wait for a decision.
var protector = Factory.Infrastructure.Security.AesGcmSecretProtector.FromConfiguration(builder.Configuration["Secrets:MasterKey"]);
var policy = Factory.Infrastructure.Security.ModelEndpointPolicy.FromConfiguration(
    builder.Configuration.GetValue<bool>(Factory.Infrastructure.Security.ModelEndpointPolicy.Setting),
    builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"));
// The configured identity prices budget reservations before each call.
builder.Services.AddSingleton(sp => new DurableWorkflowProcessor(sp.GetRequiredService<IStepQueue>(),
    new RegistryModelResolver(sp.GetRequiredService<IStepQueue>(), sp.GetRequiredService<IModelProvider>(), factoryIdentity,
        protector is null ? null : new CustomerModelProviderFactory(protector, policy))));
builder.Services.AddHostedService<WorkflowWorkerService>();

builder.Build().Run();
