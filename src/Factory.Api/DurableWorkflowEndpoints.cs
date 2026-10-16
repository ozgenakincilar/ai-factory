using Factory.Api.Security;
using Factory.Core.Authorization;
using Factory.Core.Lifecycle;
using Factory.Core.Orchestration;
using Factory.Infrastructure.Orchestration;

namespace Factory.Api;

// Asynchronous admission and status. Execution happens only in Factory.Worker processes.
public static class DurableWorkflowEndpoints
{
    public static void AddDurableWorkflows(this WebApplicationBuilder builder)
    {
        var connection = builder.Configuration.GetConnectionString("Lifecycle") ?? "";
        var store = new PostgresOrchestrationStore(connection);
        builder.Services.AddSingleton<IWorkflowRunStore>(store);
        builder.Services.AddSingleton<Factory.Core.Tenancy.ITenantUsageStore>(store);
        builder.Services.AddSingleton<Factory.Core.Tenancy.IModelRegistryStore>(store);
        builder.Services.AddSingleton<Factory.Core.Tenancy.IWorkspaceDirectory>(store);
        var policy = Factory.Infrastructure.Security.ModelEndpointPolicy.FromConfiguration(
            builder.Configuration.GetValue<bool>(Factory.Infrastructure.Security.ModelEndpointPolicy.Setting),
            builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"));
        builder.Services.AddSingleton(policy);
        // Without Secrets:MasterKey, model registration and verification answer 503.
        var protector = Factory.Infrastructure.Security.AesGcmSecretProtector.FromConfiguration(builder.Configuration["Secrets:MasterKey"]);
        if (protector is not null)
        {
            builder.Services.AddSingleton<Factory.Core.Tenancy.ISecretProtector>(protector);
            builder.Services.AddSingleton<Factory.Core.Tenancy.IModelProviderFactory>(new Factory.Infrastructure.OpenAI.CustomerModelProviderFactory(protector, policy));
        }
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(new PostgresRunEventListener(connection));
        builder.Services.AddSingleton<WorkflowEventRelay>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkflowEventRelay>());
    }

    public static void MapDurableWorkflows(this WebApplication app)
    {
        app.MapHub<WorkflowHub>(WorkflowHub.Path).RequireAuthorization();
        var group = app.MapGroup("/api/projects/{projectId}/runs").RequireAuthorization();
        // workflowVersion "decomposition-v1" selects a decomposed run: the Planner splits the request into
        // parts; an organization Owner/Admin approves the plan (decision "plan_approval") before parts run
        // in parallel on their routed models.
        group.MapPost("", (string projectId, HttpContext http, IWorkflowRunStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor =>
            {
                var header = http.Request.Headers["Idempotency-Key"];
                if (header.Count != 1 || !Guid.TryParseExact(header.ToString(), "D", out var key) || key == Guid.Empty)
                    return Error(400, "invalid_idempotency_key");
                var command = await LifecycleEndpoints.Read(http, projectId, null, null, key, allowDecomposition: true);
                var submission = await store.SubmitAsync(actor, key, command.Request, command.WorkflowVersion, http.RequestAborted);
                http.Response.Headers.Location = $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{submission.RunId:D}";
                if (submission.Replayed) http.Response.Headers["Idempotency-Replayed"] = "true";
                return Results.Json(submission, statusCode: 202);
            }));
        // The plan with each part's routed tier, for the approval popup. Same audience as the run.
        group.MapGet("/{runId:guid}/plan", (string projectId, Guid runId, HttpContext http, IWorkflowRunStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor => Results.Json(await store.GetPlanAsync(actor, projectId, runId, http.RequestAborted))));
        // Customer-provided models: organization Owner/Admin only. API keys are write-only.
        app.MapPost("/api/organizations/{organizationId}/models", (string organizationId, ModelRegistrationBody body, HttpContext http,
            Factory.Core.Tenancy.IModelRegistryStore store, Factory.Infrastructure.Security.ModelEndpointPolicy policy, FactoryAuthSettings auth)
            => Run(http, auth, async actor =>
            {
                if (http.RequestServices.GetService<Factory.Core.Tenancy.ISecretProtector>() is not { } protector) return Error(503, "secret_store_unavailable");
                if (!Enum.TryParse<Factory.Core.Tenancy.ModelProtocol>(body.Protocol, false, out var protocol) || string.IsNullOrWhiteSpace(body.Name)
                    || body.Name.Length > 100 || string.IsNullOrEmpty(body.ApiKey) || body.ApiKey.Length > Factory.Core.Tenancy.ModelRegistryLimits.MaxApiKeyLength
                    || body.ApiKey.Any(char.IsControl) || string.IsNullOrWhiteSpace(body.Model))
                    return Error(400, "invalid_model_registration");
                try { policy.ValidateUrl(body.BaseUrl); } catch (ArgumentException) { return Error(400, "invalid_model_endpoint"); }
                var id = Guid.NewGuid();
                var registration = new Factory.Core.Tenancy.ModelRegistration(body.Name.Trim(), protocol, body.BaseUrl!, body.Model, body.ApiKey,
                    body.InputMicrosPerMillion, body.OutputMicrosPerMillion);
                var encrypted = protector.Protect(body.ApiKey, Factory.Core.Tenancy.ModelEndpoint.SecretContext(organizationId, id));
                return Results.Json(await store.RegisterAsync(actor, organizationId, id, registration, encrypted, http.RequestAborted), statusCode: 201);
            })).RequireAuthorization();
        app.MapGet("/api/organizations/{organizationId}/models", (string organizationId, HttpContext http,
            Factory.Core.Tenancy.IModelRegistryStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor => Results.Json(await store.ListAsync(actor, organizationId, http.RequestAborted)))).RequireAuthorization();
        app.MapPost("/api/organizations/{organizationId}/models/{modelId:guid}/verify", (string organizationId, Guid modelId, HttpContext http,
            Factory.Core.Tenancy.IModelRegistryStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor =>
            {
                if (http.RequestServices.GetService<Factory.Core.Tenancy.IModelProviderFactory>() is not { } factory) return Error(503, "secret_store_unavailable");
                var endpoint = await store.GetEndpointAsync(actor, organizationId, modelId, http.RequestAborted);
                var (status, structured) = await new Factory.Infrastructure.OpenAI.ModelVerifier(factory).VerifyAsync(endpoint, http.RequestAborted);
                return Results.Json(await store.RecordVerificationAsync(actor, organizationId, modelId, status, structured, http.RequestAborted));
            })).RequireAuthorization();
        app.MapPut("/api/projects/{projectId}/model", (string projectId, ModelAssignmentBody body, HttpContext http,
            Factory.Core.Tenancy.IModelRegistryStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor => Results.Json(await store.AssignAsync(actor, projectId, body.ModelId, body.AcceptCapabilityRisk, http.RequestAborted))))
            .RequireAuthorization();
        // Durable regeneration of an existing work item (replaces the retired M6 route).
        app.MapPost("/api/projects/{projectId}/work-items/{workItemId:guid}/runs", (string projectId, Guid workItemId, HttpContext http,
            IWorkflowRunStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor =>
            {
                if (!http.Request.Headers.ContainsKey("If-Match")) return Error(428, "workflow_precondition_required");
                var tag = http.Request.Headers.IfMatch.ToString();
                if (http.Request.Headers.IfMatch.Count != 1 || !WorkItemETag.IsValid(tag, workItemId)) return Error(400, "invalid_workflow_precondition");
                var header = http.Request.Headers["Idempotency-Key"];
                if (header.Count != 1 || !Guid.TryParseExact(header.ToString(), "D", out var key) || key == Guid.Empty)
                    return Error(400, "invalid_idempotency_key");
                var command = await LifecycleEndpoints.Read(http, projectId, workItemId, tag, key);
                var submission = await store.SubmitRegenerationAsync(actor, key, workItemId, tag, command.Request, http.RequestAborted);
                http.Response.Headers.Location = $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{submission.RunId:D}";
                if (submission.Replayed) http.Response.Headers["Idempotency-Replayed"] = "true";
                return Results.Json(submission, statusCode: 202);
            })).RequireAuthorization();
        // Product Workspace listings: the caller's projects and a project's recent runs.
        app.MapGet("/api/me/projects", (HttpContext http, Factory.Core.Tenancy.IWorkspaceDirectory directory, FactoryAuthSettings auth)
            => Run(http, auth, async actor => Results.Json(await directory.GetMyProjectsAsync(actor, http.RequestAborted)))).RequireAuthorization();
        group.MapGet("", (string projectId, HttpContext http, IWorkflowRunStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor => Results.Json(await store.ListRunsAsync(actor, projectId, http.RequestAborted))));
        group.MapGet("/{runId:guid}", (string projectId, Guid runId, HttpContext http, IWorkflowRunStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor => Results.Json(await store.GetRunAsync(actor, projectId, runId, http.RequestAborted))));
        // Project usage over [from, to); defaults to the last 30 days. Generator or Human Reviewer.
        app.MapGet("/api/projects/{projectId}/usage", (string projectId, DateTimeOffset? from, DateTimeOffset? to, HttpContext http,
            IWorkflowRunStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor =>
            {
                var end = to ?? DateTimeOffset.UtcNow; var start = from ?? end.AddDays(-30);
                return Results.Json(await store.GetUsageAsync(actor, projectId, start, end, http.RequestAborted));
            })).RequireAuthorization();
        // Organization usage across its projects; Human Owner/Admin only.
        app.MapGet("/api/organizations/{organizationId}/usage", (string organizationId, DateTimeOffset? from, DateTimeOffset? to, HttpContext http,
            Factory.Core.Tenancy.ITenantUsageStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor =>
            {
                var end = to ?? DateTimeOffset.UtcNow; var start = from ?? end.AddDays(-30);
                return Results.Json(await store.GetOrganizationUsageAsync(actor, organizationId, start, end, http.RequestAborted));
            })).RequireAuthorization();
        // Current organization and project budgets; Human Owner/Admin only.
        app.MapGet("/api/organizations/{organizationId}/budgets", (string organizationId, HttpContext http,
            Factory.Core.Tenancy.ITenantUsageStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor => Results.Json(await store.GetBudgetsAsync(actor, organizationId, http.RequestAborted))))
            .RequireAuthorization();
        group.MapPost("/{runId:guid}/cancel", (string projectId, Guid runId, HttpContext http, IWorkflowRunStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor => Results.Json(await store.CancelAsync(actor, projectId, runId, http.RequestAborted))));
        group.MapPost("/{runId:guid}/decisions/{decisionId:guid}", (string projectId, Guid runId, Guid decisionId, HttpContext http,
            IWorkflowRunStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor => Results.Json(await store.DecideAsync(actor, projectId, runId, decisionId,
                await ReadChoice(http), http.RequestAborted))));
    }

    // Strict body: exactly {"choice": "<code>"}, at most 1 KiB.
    private static async Task<string> ReadChoice(HttpContext http)
    {
        if (!http.Request.HasJsonContentType()) throw new OrchestrationException(OrchestrationError.InvalidCommand);
        var buffer = new byte[1025]; var length = 0;
        while (length < buffer.Length)
        {
            var count = await http.Request.Body.ReadAsync(buffer.AsMemory(length), http.RequestAborted);
            if (count == 0) break; length += count;
        }
        if (length > 1024) throw new OrchestrationException(OrchestrationError.InvalidCommand);
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, length), new System.Text.Json.JsonDocumentOptions { MaxDepth = 2 });
            var fields = document.RootElement.EnumerateObject().ToArray();
            if (fields.Length != 1 || fields[0].Name != "choice" || fields[0].Value.ValueKind != System.Text.Json.JsonValueKind.String ||
                !DecisionCodes.IsCode(fields[0].Value.GetString()))
                throw new OrchestrationException(OrchestrationError.InvalidCommand);
            return fields[0].Value.GetString()!;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        { throw new OrchestrationException(OrchestrationError.InvalidCommand); }
    }

    private static async Task<IResult> Run(HttpContext http, FactoryAuthSettings settings, Func<TrustedIdentity, Task<IResult>> execute)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = settings.Identity(http.User);
        if (actor is null || (settings.LocalHttp && (http.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address))))
            return Results.Forbid();
        try { return await execute(actor); }
        catch (LifecycleException ex) when (ex.Error == LifecycleError.InvalidCommand) { return Error(400, "invalid_workflow_command"); }
        catch (OrchestrationException ex)
        {
            return ex.Error switch
            {
                OrchestrationError.InvalidCommand => Error(400, "invalid_workflow_command"),
                OrchestrationError.Forbidden => Error(403, "workflow_forbidden"),
                OrchestrationError.NotFound => Error(404, "workflow_not_found"),
                OrchestrationError.Conflict => Error(409, "workflow_conflict"),
                OrchestrationError.StaleRevision => Error(412, "workflow_revision_changed"),
                OrchestrationError.CapabilityUnverified => Error(409, "model_capability_unverified"),
                OrchestrationError.Capacity => Error(429, "workflow_capacity"),
                OrchestrationError.Integrity => Error(500, "workflow_integrity_failure"),
                OrchestrationError.TransactionAborted => Error(503, "workflow_transaction_aborted"),
                _ => Error(503, "workflow_outcome_not_confirmed")
            };
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { return Results.StatusCode(499); }
    }

    private static IResult Error(int status, string code) => Results.Problem(statusCode: status, title: code);
}

public sealed record ModelRegistrationBody(string? Name, string? Protocol, string? BaseUrl, string? Model, string? ApiKey,
    long? InputMicrosPerMillion = null, long? OutputMicrosPerMillion = null);
public sealed record ModelAssignmentBody(Guid? ModelId, bool AcceptCapabilityRisk);
