
using System.ClientModel;
using Factory.Api;
using Factory.Api.Security;
using Factory.Orchestration.Workflows;
using Factory.Core.Agents;
using Factory.Core.Models;
using Factory.Infrastructure.OpenAI;
using Factory.Orchestration.Agents;
using OpenAI.Chat;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.CamelCase, allowIntegerValues: false)));

// ======================================================
// 1. APPLICATION CONFIGURATION
// ======================================================

Console.WriteLine("==================================");
Console.WriteLine(" Universal AI Factory");
Console.WriteLine("==================================");

Console.WriteLine(
    $"Environment: {builder.Environment.EnvironmentName}");

Console.WriteLine(
    $"Application: {builder.Environment.ApplicationName}");

// CreateBuilder supplies the precedence (highest first): command line, environment,
// Development User Secrets, environment-specific JSON, appsettings.json.
// Never append User Secrets here: doing so would override operator configuration.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options =>
    options.ThrowOnBadRequest = true);
// Binding diagnostics may contain JSON paths/values or exception objects.
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.RequestDelegateFactory", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.RequestDelegateGenerator", LogLevel.None);

// ======================================================
// 2. OPENAI CONFIGURATION
// ======================================================

var apiKey = builder.Configuration["OpenAI:ApiKey"];

var model = builder.Configuration["OpenAI:Model"]
    ?? "gpt-4o-mini";

Console.WriteLine(
    $"OpenAI API Key Available: {!string.IsNullOrWhiteSpace(apiKey)}");


OpenAiConfiguration.ValidateApiKey(apiKey);

// ======================================================
// 3. DEPENDENCY INJECTION
// ======================================================

// OpenAI ChatClient
builder.Services.AddSingleton(
    new ChatClient(model, apiKey));

// Model Provider
builder.Services.AddSingleton<IModelProvider>(sp =>
    new OpenAiModelProvider(
        sp.GetRequiredService<ChatClient>(),
        model));

// M6 uses a separate client: zero SDK retries and a bounded network timeout.
// Existing ephemeral endpoints retain their provider configuration and behavior.
builder.Services.AddKeyedSingleton<IModelProvider>("lifecycle", (sp, key) =>
    new OpenAiModelProvider(new ChatClient(model, new ApiKeyCredential(apiKey!), new OpenAI.OpenAIClientOptions
    {
        RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0),
        NetworkTimeout = TimeSpan.FromSeconds(90)
    }), model));

// Analyst Agent
builder.Services.AddTransient<AnalystAgent>();
builder.Services.AddTransient<ArchitectAgent>();
builder.Services.AddTransient<AnalysisArchitectureWorkflow>();

builder.Services.AddTransient<IAgent>(sp =>
    sp.GetRequiredService<AnalystAgent>());

// ======================================================
// 4. BUILD APPLICATION
// ======================================================

builder.AddFactorySecurity();
builder.AddReviewDecisions();
builder.AddLifecycle();
builder.AddDurableWorkflows();
var legacyGeneration = LegacyGeneration.Enabled(builder.Configuration, builder.Environment);
var app = builder.Build();
app.UseMiddleware<SafeApiExceptionMiddleware>();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapReviewEndpoints();
app.MapLifecycle(legacyGeneration);
app.MapDurableWorkflows();
app.MapDevelopments();
if (legacyGeneration)
    app.MapPost("/api/workflows/analysis-architecture", WorkflowEndpoint.ExecuteAsync)
        .RequireAuthorization().AddEndpointFilter<ProjectGenerationFilter>();
else
    foreach (var retired in new[] { "/api/workflows/analysis-architecture", "/api/agents/analyst", "/api/agents/architect" })
        app.MapPost(retired, LegacyGeneration.Retired).RequireAuthorization();

// ======================================================
// 5. HEALTH ENDPOINT
// ======================================================

app.MapGet("/health", () =>
{
    return Results.Ok(new
    {
        status = "healthy",
        application = "Universal AI Factory",
        environment = app.Environment.EnvironmentName
    });
}).AllowAnonymous();

// ======================================================
// 6. ANALYST AGENT ENDPOINT
// ======================================================

// Retired unless explicitly re-enabled in Development/Testing (see LegacyGeneration).
if (legacyGeneration)
{
    app.MapPost("/api/agents/analyst", (
        AgentRequest request,
        AnalystAgent agent,
        HttpContext context,
        CancellationToken cancellationToken) => ExecuteAgent(request, agent, context, cancellationToken))
        .RequireAuthorization().AddEndpointFilter<ProjectGenerationFilter>();

    app.MapPost("/api/agents/architect", (
        AgentRequest request,
        ArchitectAgent agent,
        HttpContext context,
        CancellationToken cancellationToken) => ExecuteAgent(request, agent, context, cancellationToken))
        .RequireAuthorization().AddEndpointFilter<ProjectGenerationFilter>();
}

async Task<IResult> ExecuteAgent(AgentRequest request, IAgent agent, HttpContext context, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(request.ProjectId) ||
        string.IsNullOrWhiteSpace(request.Task))
    {
        return Results.BadRequest(new
        {
            error = "ProjectId and Task are required."
        });
    }

    try
    {
        var result = await agent.ExecuteAsync(
            request,
            cancellationToken);

        if (!result.Success)
        {
            var statusCode = result.FailureKind switch
            {
                AgentFailureKind.InvalidRequest => 400,
                AgentFailureKind.Refused => 422,
                _ => 502
            };
            return Results.Json(result, statusCode: statusCode);
        }

        return Results.Ok(result);
    }

    // OpenAI API errors
    catch (ClientResultException ex)
    {
        app.Logger.LogError("Agent request {TraceId}: provider_failure; HTTP {StatusCode}; " +
            "provider HTTP {ProviderStatus}; provider request {ProviderRequestId}; exception type {ExceptionType}",
            context.TraceIdentifier, 502, ex.Status, SafeApiDiagnostics.ProviderRequestId(ex), ex.GetType().FullName);

        return Results.Problem(
            title: "OpenAI API request failed",
            detail: "Check server logs for the HTTP status code and request ID.",
            statusCode: 502);
    }

    // Request cancellation
    catch (OperationCanceledException)
        when (cancellationToken.IsCancellationRequested)
    {
        app.Logger.LogInformation("Agent request {TraceId}: request_cancelled; HTTP {StatusCode}",
            context.TraceIdentifier, 499);
        return Results.StatusCode(499);
    }

    // Unexpected errors
    catch (Exception ex)
    {
        app.Logger.LogError("Agent request {TraceId}: unexpected_agent_failure; HTTP {StatusCode}; exception type {ExceptionType}",
            context.TraceIdentifier, 500, ex.GetType().FullName);

        return Results.Problem(
            title: "Agent execution failed",
            detail: "An unexpected error occurred.",
            statusCode: 500);
    }
}

// ======================================================
// 7. START APPLICATION
// ======================================================

Console.WriteLine("Factory configuration completed.");

app.Run();

// Expose the entry point to WebApplicationFactory integration tests.
public partial class Program { }
