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
        builder.Services.AddSingleton<IWorkflowRunStore>(new PostgresOrchestrationStore(connection));
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(new PostgresRunEventListener(connection));
        builder.Services.AddSingleton<WorkflowEventRelay>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkflowEventRelay>());
    }

    public static void MapDurableWorkflows(this WebApplication app)
    {
        app.MapHub<WorkflowHub>(WorkflowHub.Path).RequireAuthorization();
        var group = app.MapGroup("/api/projects/{projectId}/runs").RequireAuthorization();
        group.MapPost("", (string projectId, HttpContext http, IWorkflowRunStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor =>
            {
                var header = http.Request.Headers["Idempotency-Key"];
                if (header.Count != 1 || !Guid.TryParseExact(header.ToString(), "D", out var key) || key == Guid.Empty)
                    return Error(400, "invalid_idempotency_key");
                var command = await LifecycleEndpoints.Read(http, projectId, null, null, key);
                var submission = await store.SubmitAsync(actor, key, command.Request, http.RequestAborted);
                http.Response.Headers.Location = $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{submission.RunId:D}";
                if (submission.Replayed) http.Response.Headers["Idempotency-Replayed"] = "true";
                return Results.Json(submission, statusCode: 202);
            }));
        group.MapGet("/{runId:guid}", (string projectId, Guid runId, HttpContext http, IWorkflowRunStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor => Results.Json(await store.GetRunAsync(actor, projectId, runId, http.RequestAborted))));
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
