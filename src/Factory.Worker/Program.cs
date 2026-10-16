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

// M9.3 verification jobs (opt-in): Templates:Root is the registry directory and
// Verification:RunnerImage the immutable runner image id (scripts/Build-FactoryRunner.ps1).
if (builder.Configuration.GetValue<bool>("Verification:Enabled"))
{
    var templates = builder.Configuration["Templates:Root"];
    var runner = builder.Configuration["Verification:RunnerImage"];
    if (string.IsNullOrWhiteSpace(templates) || string.IsNullOrWhiteSpace(runner))
        throw new InvalidOperationException("Verification needs Templates:Root and Verification:RunnerImage.");
    var store = new PostgresOrchestrationStore(connection);
    builder.Services.AddSingleton(new Factory.Orchestration.Verification.VerificationProcessor(store,
        new Factory.Infrastructure.Templates.FileSystemTemplateCatalog(templates),
        new Factory.Infrastructure.Execution.DockerSandbox(new Factory.Infrastructure.Execution.DockerSandboxOptions(runner,
            Factory.Core.Execution.SandboxLimits.Default))));
    builder.Services.AddHostedService<VerificationWorkerService>();
}

// M10.3 development jobs (opt-in): the Developer Agent on approved review artifacts. Needs the same
// template root and runner image, plus Development:Model and its verified prices (micro-USD per 1M
// tokens). Caps are user decisions: USD 0.40 per job and USD 2 in total by default.
if (builder.Configuration.GetValue<bool>("Development:Enabled"))
{
    var templates = builder.Configuration["Templates:Root"];
    var runner = builder.Configuration["Verification:RunnerImage"];
    var model = builder.Configuration["Development:Model"] ?? "gpt-5.4-mini";
    var input = builder.Configuration.GetValue<long?>("Development:InputMicrosPerMillion");
    var output = builder.Configuration.GetValue<long?>("Development:OutputMicrosPerMillion");
    if (string.IsNullOrWhiteSpace(templates) || string.IsNullOrWhiteSpace(runner) || input is not > 0 || output is not > 0)
        throw new InvalidOperationException("Development needs Templates:Root, Verification:RunnerImage and the model prices.");
    var apiKey = builder.Configuration["OpenAI:ApiKey"];
    OpenAiConfiguration.ValidateApiKey(apiKey);
    var client = new ChatClient(model, new ApiKeyCredential(apiKey!), new OpenAI.OpenAIClientOptions
    {
        RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0),
        NetworkTimeout = TimeSpan.FromSeconds(240)
    });
    var sandbox = new Factory.Infrastructure.Execution.DockerSandboxOptions(runner, Factory.Core.Execution.SandboxLimits.Default);
    var limits = Factory.Core.Development.DevelopmentLimits.Default with
    {
        CostCapMicros = builder.Configuration.GetValue("Development:JobCapMicros", Factory.Core.Development.DevelopmentLimits.Default.CostCapMicros)
    };
    builder.Services.AddSingleton(new Factory.Orchestration.Development.DevelopmentProcessor(new PostgresOrchestrationStore(connection),
        new Factory.Infrastructure.Templates.FileSystemTemplateCatalog(templates),
        new Factory.Orchestration.Development.DevelopmentModel(model, new OpenAiModelProvider(client, model), input.Value, output.Value),
        new Factory.Infrastructure.Execution.DockerWorkspaceRunner(sandbox), new Factory.Infrastructure.Execution.DockerSandbox(sandbox),
        new Factory.Infrastructure.Execution.GitWorkspaceDiffer(sandbox), limits,
        builder.Configuration.GetValue("Development:TotalCapMicros", 2_000_000L)));
    builder.Services.AddHostedService<DevelopmentWorkerService>();

    // M11.3 pull requests (opt-in): GitHub:Repository and a token limited to that repository. The
    // Factory pushes only verified, evidence-matching commits and never merges.
    var repository = builder.Configuration["GitHub:Repository"];
    if (!string.IsNullOrWhiteSpace(repository))
    {
        var token = builder.Configuration["GitHub:Token"];
        if (!Factory.Core.Development.PullRequestText.IsRepository(repository) || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Pull requests need GitHub:Repository (owner/name) and GitHub:Token.");
        builder.Services.AddSingleton(new Factory.Orchestration.Development.PullRequestProcessor(new PostgresOrchestrationStore(connection),
            new Factory.Infrastructure.Templates.FileSystemTemplateCatalog(templates),
            new Factory.Infrastructure.GitHub.GitHubPullRequestPublisher(new Factory.Infrastructure.GitHub.GitHubOptions(repository, token),
                new HttpClient { Timeout = TimeSpan.FromSeconds(60) })));
        builder.Services.AddHostedService<PullRequestWorkerService>();
    }
}

builder.Build().Run();
