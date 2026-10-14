using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Factory.Api.Security;
using Factory.Core.Authorization;
using Factory.Core.Reviews;
using Factory.Infrastructure.Reviews;
using Microsoft.EntityFrameworkCore;

namespace Factory.Api;

public static class ReviewEndpoints
{
    public static void AddReviewDecisions(this WebApplicationBuilder builder)
    {
        var connection = builder.Configuration.GetConnectionString("Reviews");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("review_storage_not_configured");
        // No EF diagnostic logger: database messages/parameters may contain artifact contents or reasons.
        builder.Services.AddSingleton<IReviewDecisionStore>(new PostgresReviewDecisionStore(
            new DbContextOptionsBuilder<ReviewDbContext>().UseNpgsql(connection).Options));
    }

    public static void MapReviewEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/reviews").RequireAuthorization();
        group.MapGet("/{caseId:guid}", (string projectId, Guid caseId, HttpContext http, IReviewDecisionStore store,
            FactoryAuthSettings auth, ILoggerFactory logs) => Run(http, auth, logs, async identity =>
        {
            var snapshot = await store.GetAsync(identity, projectId, caseId, http.RequestAborted);
            http.Response.Headers.ETag = ETag(snapshot.Id, snapshot.Revision);
            return Results.Json(snapshot);
        }));
        group.MapGet("/{caseId:guid}/artifacts/{version:int}", (string projectId, Guid caseId, int version, HttpContext http,
            IReviewDecisionStore store, FactoryAuthSettings auth, ILoggerFactory logs) => Run(http, auth, logs, async identity =>
        {
            var artifact = await store.GetArtifactAsync(identity, projectId, caseId, version, http.RequestAborted);
            http.Response.Headers.ETag = "\"sha256-" + artifact.ContentHash + "\"";
            return Results.Bytes(artifact.GetContent(), "application/json; charset=utf-8");
        }));
        group.MapPost("/{caseId:guid}", (string projectId, Guid caseId, HttpContext http, IReviewDecisionStore store,
            FactoryAuthSettings auth, ILoggerFactory logs) => Run(http, auth, logs, async identity =>
        {
            if (!http.Request.Headers.ContainsKey("If-Match")) return Error(428, "review_precondition_required");
            var etag = http.Request.Headers.IfMatch;
            var prefix = "\"review-" + caseId.ToString("D") + "-r";
            var tag = etag.ToString();
            if (etag.Count != 1 || !tag.StartsWith(prefix, StringComparison.Ordinal) || !tag.EndsWith('"') ||
                !long.TryParse(tag.AsSpan(prefix.Length, tag.Length - prefix.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
                revision < 1 || tag != ETag(caseId, revision)) return Error(400, "invalid_review_precondition");
            var keyHeader = http.Request.Headers["Idempotency-Key"];
            if (keyHeader.Count != 1 || !Guid.TryParseExact(keyHeader.ToString(), "D", out var key) || key == Guid.Empty)
                return Error(400, "invalid_idempotency_key");
            var command = await ReadCommand(http, revision, key);
            var result = await store.DecideAsync(identity, projectId, caseId, command, http.RequestAborted);
            if (result.Replayed) http.Response.Headers["Idempotency-Replayed"] = "true";
            else http.Response.Headers.ETag = ETag(caseId, result.Snapshot.Revision);
            // Identical historical body; replay never emits an old ETag as the current validator.
            return Results.Json(result.Snapshot);
        }));
    }

    public static string ETag(Guid id, long revision) => $"\"review-{id:D}-r{revision.ToString(CultureInfo.InvariantCulture)}\"";

    private static async Task<DecisionCommand> ReadCommand(HttpContext http, long revision, Guid key)
    {
        if (!http.Request.HasJsonContentType()) throw new ReviewCommandException(ReviewError.InvalidCommand);
        var buffer = new byte[16385];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await http.Request.Body.ReadAsync(buffer.AsMemory(count), http.RequestAborted);
            if (read == 0) break;
            count += read;
        }
        if (count > 16384) throw new ReviewCommandException(ReviewError.InvalidCommand);
        try
        {
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != 4 || names.Distinct(StringComparer.Ordinal).Count() != 4 ||
                names.Except(new[] { "decision", "artifactVersion", "artifactSha256", "reason" }, StringComparer.Ordinal).Any()) throw new JsonException();
            var action = root.GetProperty("decision").GetString() switch
            {
                "approve" => ReviewDecision.Approve, "reject" => ReviewDecision.Reject,
                "requestChanges" => ReviewDecision.RequestChanges, _ => throw new JsonException()
            };
            return new(revision, root.GetProperty("artifactVersion").GetInt32(), root.GetProperty("artifactSha256").GetString()!,
                action, root.GetProperty("reason").GetString(), key);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new ReviewCommandException(ReviewError.InvalidCommand); }
    }

    private static async Task<IResult> Run(HttpContext http, FactoryAuthSettings settings, ILoggerFactory logs,
        Func<TrustedIdentity, Task<IResult>> execute)
    {
        http.Response.Headers.CacheControl = "no-store";
        var identity = settings.Identity(http.User);
        if (identity?.Kind != PrincipalKind.Human || (settings.LocalHttp &&
            (http.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address)))) return Results.Forbid();
        try { return await execute(identity); }
        catch (ReviewCommandException ex)
        {
            var (status, code) = ex.Error switch
            {
                ReviewError.Forbidden => (403, "review_forbidden"), ReviewError.NotFound => (404, "review_not_found"),
                ReviewError.StaleRevision => (412, "review_revision_changed"), ReviewError.ArtifactMismatch => (409, "review_artifact_mismatch"),
                ReviewError.AlreadyDecided => (409, "review_already_decided"), ReviewError.IdempotencyConflict => (409, "idempotency_conflict"),
                _ => (400, "invalid_review_command")
            };
            return Error(status, code);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { return Results.StatusCode(499); }
        catch (Exception ex) when (ex is DbException or DbUpdateException or ReviewIntegrityException)
        {
            var databaseFailure = ex is DbUpdateException update ? update.InnerException : ex;
            var integrity = ex is ReviewIntegrityException || databaseFailure is Npgsql.PostgresException { SqlState: var state } && state.StartsWith("23", StringComparison.Ordinal);
            logs.CreateLogger("ReviewDecisions").LogWarning("Review request {TraceId}: {FailureCode}; exception type {ExceptionType}",
                http.TraceIdentifier, integrity ? "review_integrity_failure" : "review_storage_unavailable", ex.GetType().FullName);
            return Error(integrity ? 500 : 503, integrity ? "review_integrity_failure" : "review_storage_unavailable");
        }
    }
    private static IResult Error(int status, string code) => Results.Problem(statusCode: status, title: code);
}
