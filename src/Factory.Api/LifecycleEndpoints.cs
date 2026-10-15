using System.Text.Json;
using Factory.Api.Security;
using Factory.Core.Analysis;
using Factory.Core.Authorization;
using Factory.Core.Lifecycle;
using Factory.Core.Models;
using Factory.Core.Reviews;
using Factory.Core.Workflows;
using Factory.Infrastructure.Lifecycle;
using Factory.Infrastructure.Reviews;
using Factory.Orchestration.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace Factory.Api;

public static class LifecycleEndpoints
{
    public static void AddLifecycle(this WebApplicationBuilder builder)
    {
        var connection = builder.Configuration.GetConnectionString("Lifecycle") ?? "";
        builder.Services.AddSingleton<ILifecycleStore>(new PostgresLifecycleStore(connection));
        builder.Services.AddSingleton<IReviewStore>(new PostgresReviewStore(new DbContextOptionsBuilder<ReviewDbContext>().UseNpgsql(connection).Options));
        builder.Services.AddSingleton(sp => new LifecycleService(sp.GetRequiredService<ILifecycleStore>(), sp.GetRequiredService<IReviewStore>(),
            sp.GetRequiredKeyedService<IModelProvider>("lifecycle")));
    }

    public static void MapLifecycle(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}").RequireAuthorization();
        group.MapPost("/work-items", (string projectId, HttpContext http, LifecycleService service, FactoryAuthSettings auth)
            => Run(http, auth, async actor => await Execute(projectId, null, http, actor, service)));
        group.MapPost("/work-items/{workItemId:guid}/analysis-architecture", (string projectId, Guid workItemId, HttpContext http, LifecycleService service, FactoryAuthSettings auth)
            => Run(http, auth, async actor => await Execute(projectId, workItemId, http, actor, service)));
        group.MapGet("/work-items/{workItemId:guid}", (string projectId, Guid workItemId, HttpContext http, ILifecycleStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor =>
            {
                var result = await store.GetWorkItemAsync(actor, projectId, workItemId, http.RequestAborted);
                http.Response.Headers.ETag = result.ETag;
                return Results.Json(result);
            }));
        group.MapGet("/workflow-runs/{runId:guid}", (string projectId, Guid runId, HttpContext http, ILifecycleStore store, FactoryAuthSettings auth)
            => Run(http, auth, async actor => Results.Json(await store.GetRunAsync(actor, projectId, runId, http.RequestAborted))));
    }

    private static async Task<IResult> Execute(string project, Guid? workItem, HttpContext http, TrustedIdentity actor, LifecycleService service)
    {
        string? tag = null;
        if (workItem is { } id)
        {
            if (!http.Request.Headers.ContainsKey("If-Match")) return Error(428, "lifecycle_precondition_required");
            tag = http.Request.Headers.IfMatch.ToString();
            if (http.Request.Headers.IfMatch.Count != 1 || !WorkItemETag.IsValid(tag, id)) return Error(400, "invalid_lifecycle_precondition");
        }
        var header = http.Request.Headers["Idempotency-Key"];
        if (header.Count != 1 || !Guid.TryParseExact(header.ToString(), "D", out var key) || key == Guid.Empty)
            return Error(400, "invalid_idempotency_key");
        var command = await Read(http, project, workItem, tag, key);
        var response = await service.ExecuteAsync(actor, command, http.RequestAborted);
        http.Response.Headers.Location = $"/api/projects/{Uri.EscapeDataString(project)}/workflow-runs/{response.Outcome.RunId:D}";
        if (response.Replayed) http.Response.Headers["Idempotency-Replayed"] = "true";
        return Results.Json(response.Outcome, statusCode: response.Outcome.HttpStatus);
    }

    // Shared with durable workflow admission: same strict body contract and limits.
    internal static async Task<GenerationCommand> Read(HttpContext http, string project, Guid? workItem, string? tag, Guid key)
    {
        if (!http.Request.HasJsonContentType()) throw new LifecycleException(LifecycleError.InvalidCommand);
        var buffer = new byte[LifecycleLimits.RequestBytes + 1]; var length = 0;
        while (length < buffer.Length)
        {
            var count = await http.Request.Body.ReadAsync(buffer.AsMemory(length), http.RequestAborted);
            if (count == 0) break; length += count;
        }
        if (length > LifecycleLimits.RequestBytes) throw new LifecycleException(LifecycleError.InvalidCommand);
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 5 });
            var root = document.RootElement;
            ExactFields(root, ["task", "context", "confirmedRequirements", "workflowVersion"]);
            var sources = root.GetProperty("confirmedRequirements");
            List<ConfirmedRequirement>? confirmed = null;
            if (sources.ValueKind != JsonValueKind.Null)
            {
                confirmed = [];
                foreach (var source in sources.EnumerateArray())
                {
                    ExactFields(source, ["id", "description"]);
                    confirmed.Add(new(source.GetProperty("id").GetString()!, source.GetProperty("description").GetString()!));
                }
            }
            var result = new GenerationCommand(key, workItem, tag, root.GetProperty("workflowVersion").GetString()!,
                new WorkflowRequest(project, root.GetProperty("task").GetString()!, root.GetProperty("context").GetString()!, confirmed));
            GenerationFingerprint.Validate(result); return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        { throw new LifecycleException(LifecycleError.InvalidCommand); }
    }
    private static void ExactFields(JsonElement element, string[] expected)
    {
        var names = element.EnumerateObject().Select(x => x.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Except(expected, StringComparer.Ordinal).Any())
            throw new JsonException();
    }
    private static async Task<IResult> Run(HttpContext http, FactoryAuthSettings settings, Func<TrustedIdentity, Task<IResult>> execute)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = settings.Identity(http.User);
        if (actor is null || (settings.LocalHttp && (http.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address))))
            return Results.Forbid();
        try { return await execute(actor); }
        catch (LifecycleException ex)
        {
            if (ex.RunId is { } run) http.Response.Headers["X-Workflow-Id"] = run.ToString("D");
            var status = ex.Error switch
            {
                LifecycleError.InvalidCommand => 400, LifecycleError.Forbidden => 403, LifecycleError.NotFound => 404,
                LifecycleError.StaleRevision => 412, LifecycleError.Conflict or LifecycleError.Expired => 409,
                LifecycleError.Capacity => 429, LifecycleError.Integrity => 500, _ => 503
            };
            return Error(status, ex.Error switch
            {
                LifecycleError.OutcomeUnknown => "lifecycle_outcome_not_confirmed", LifecycleError.Integrity => "lifecycle_integrity_failure",
                LifecycleError.Forbidden => "lifecycle_forbidden", LifecycleError.NotFound => "lifecycle_not_found",
                LifecycleError.StaleRevision => "lifecycle_revision_changed", LifecycleError.Capacity => "lifecycle_capacity",
                LifecycleError.InvalidCommand => "invalid_lifecycle_command", LifecycleError.TransactionAborted => "lifecycle_transaction_aborted", _ => "lifecycle_conflict"
            });
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { return Results.StatusCode(499); }
    }
    private static IResult Error(int status, string code) => Results.Problem(statusCode: status, title: code);
}
