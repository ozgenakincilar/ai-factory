using System.ClientModel;
using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Infrastructure.OpenAI;
using Factory.Infrastructure.Orchestration;
using Factory.Orchestration.Durable;
using Factory.Worker;
using OpenAI.Chat;

// Standard precedence (highest first): command line, environment, Development User Secrets, JSON.
var builder = Host.CreateApplicationBuilder(args);

var apiKey = builder.Configuration["OpenAI:ApiKey"];
OpenAiConfiguration.ValidateApiKey(apiKey);
var model = builder.Configuration["OpenAI:Model"] ?? "gpt-4o-mini";
var connection = builder.Configuration.GetConnectionString("Orchestration");
if (string.IsNullOrWhiteSpace(connection))
    throw new InvalidOperationException("ConnectionStrings:Orchestration is not configured; use the restricted worker role.");

builder.Services.AddSingleton<IStepQueue>(new PostgresOrchestrationStore(connection));
// No SDK retries: a lost or failed call is recorded, never silently repeated by the client.
builder.Services.AddSingleton<IModelProvider>(new OpenAiModelProvider(new ChatClient(model, new ApiKeyCredential(apiKey!),
    new OpenAI.OpenAIClientOptions
    {
        RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0),
        NetworkTimeout = TimeSpan.FromSeconds(90)
    }), model));
// Customer models need the master key (Secrets:MasterKey) to decrypt their credential and the
// endpoint egress policy. Without the key, runs assigned to customer models wait for a decision.
var protector = Factory.Infrastructure.Security.AesGcmSecretProtector.FromConfiguration(builder.Configuration["Secrets:MasterKey"]);
var policy = Factory.Infrastructure.Security.ModelEndpointPolicy.FromConfiguration(
    builder.Configuration.GetValue<bool>(Factory.Infrastructure.Security.ModelEndpointPolicy.Setting),
    builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"));
// The configured identity prices budget reservations before each call.
builder.Services.AddSingleton(sp => new DurableWorkflowProcessor(sp.GetRequiredService<IStepQueue>(),
    new RegistryModelResolver(sp.GetRequiredService<IStepQueue>(), sp.GetRequiredService<IModelProvider>(), new ModelIdentity("openai", model),
        protector is null ? null : new CustomerModelProviderFactory(protector, policy))));
builder.Services.AddHostedService<WorkflowWorkerService>();

builder.Build().Run();
