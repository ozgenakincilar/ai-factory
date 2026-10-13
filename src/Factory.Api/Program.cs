
using System.ClientModel;
using Factory.Api;
using Factory.Api.Security;
using Factory.Orchestration.Workflows;
using Factory.Core.Agents;
using Factory.Core.Models;
using Factory.Infrastructure.OpenAI;
using Factory.Orchestration.Agents;
using Microsoft.Extensions.Configuration;
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

// User Secrets for local development
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets(
        typeof(Program).Assembly,
        optional: false,
        reloadOnChange: false);

    Console.WriteLine("User Secrets loading attempted.");
}

// ======================================================
// 2. OPENAI CONFIGURATION
// ======================================================

var apiKey = builder.Configuration["OpenAI:ApiKey"];

var model = builder.Configuration["OpenAI:Model"]
    ?? "gpt-4o-mini";

Console.WriteLine(
    $"OpenAI API Key Available: {!string.IsNullOrWhiteSpace(apiKey)}");

Console.WriteLine($"OpenAI Model: {model}");

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
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapPost("/api/workflows/analysis-architecture", WorkflowEndpoint.ExecuteAsync)
    .RequireAuthorization().AddEndpointFilter<ProjectGenerationFilter>();

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

app.MapPost("/api/agents/analyst", (
    AgentRequest request,
    AnalystAgent agent,
    CancellationToken cancellationToken) => ExecuteAgent(request, agent, cancellationToken))
    .RequireAuthorization().AddEndpointFilter<ProjectGenerationFilter>();

app.MapPost("/api/agents/architect", (
    AgentRequest request,
    ArchitectAgent agent,
    CancellationToken cancellationToken) => ExecuteAgent(request, agent, cancellationToken))
    .RequireAuthorization().AddEndpointFilter<ProjectGenerationFilter>();

async Task<IResult> ExecuteAgent(AgentRequest request, IAgent agent, CancellationToken cancellationToken)
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
        var response = ex.GetRawResponse();
        string? requestId = null;
        response?.Headers.TryGetValue("x-request-id", out requestId);

        // Only log the expected request-ID format, never arbitrary upstream text.
        var safeRequestId = requestId is not null &&
            System.Text.RegularExpressions.Regex.IsMatch(
                requestId, @"\A(?:req_)?[a-fA-F0-9-]{16,64}\z")
            ? requestId
            : "unavailable";

        app.Logger.LogError(
            "OpenAI request failed. " +
            "HTTP Status: {StatusCode}, " +
            "Request ID: {RequestId}, " +
            "Response Body Present: {HasResponseBody}, " +
            "Exception Type: {ExceptionType}",
            ex.Status,
            safeRequestId,
            response?.Content is { } body && body.ToMemory().Length > 0,
            ex.GetType().FullName);

        return Results.Problem(
            title: "OpenAI API request failed",
            detail: "Check server logs for the HTTP status code and request ID.",
            statusCode: 502);
    }

    // Request cancellation
    catch (OperationCanceledException)
        when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }

    // Unexpected errors
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Unexpected agent execution failure.");

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
