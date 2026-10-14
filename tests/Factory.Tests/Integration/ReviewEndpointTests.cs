using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Factory.Api;
using Factory.Core.Models;
using Factory.Core.Reviews;
using Factory.Infrastructure.Reviews;
using Factory.Tests.Reviews;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Factory.Tests.Integration;

public class ReviewEndpointTests
{
    internal static string Route(DecisionSeed s) => $"/api/projects/{s.Case.ProjectId}/reviews/{s.Case.Id:D}";
    internal static HttpRequestMessage Command(DecisionSeed s, string action = "approve", Guid? key = null, string? etag = null,
        string? hash = null, int version = 1, string? reason = "private-review-reason")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route(s)) {
            Content = JsonContent.Create(new { decision = action, artifactVersion = version, artifactSha256 = hash ?? s.Artifact.ContentHash, reason }) };
        request.Headers.TryAddWithoutValidation("If-Match", etag ?? ReviewEndpoints.ETag(s.Case.Id, s.Case.Revision));
        request.Headers.TryAddWithoutValidation("Idempotency-Key", (key ?? Guid.NewGuid()).ToString("D"));
        return request;
    }
    private static HttpClient Client(ReviewFactory factory, DecisionSeed s)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestSecurity.Token(subject: s.Principal.Subject));
        return client;
    }

    [DecisionPostgresTheory]
    [InlineData("approve", "approved")]
    [InlineData("reject", "rejected")]
    [InlineData("requestChanges", "changesRequested")]
    public async Task AuthenticatedDecision_ExactBytesHistoricalReplayAndNoExecution(string action, string state)
    {
        var s = await DecisionTestDatabase.Seed(); using var factory = new ReviewFactory(); using var client = Client(factory, s);
        var snapshot = await client.GetAsync(Route(s)); Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        Assert.Equal(ReviewEndpoints.ETag(s.Case.Id, 1), snapshot.Headers.ETag!.Tag);
        Assert.True(snapshot.Headers.CacheControl!.NoStore);
        var artifact = await client.GetAsync(Route(s) + "/artifacts/1");
        Assert.Equal(HttpStatusCode.OK, artifact.StatusCode); Assert.Equal(s.Artifact.GetContent(), await artifact.Content.ReadAsByteArrayAsync());
        var key = Guid.NewGuid();
        using var request = Command(s, action, key); var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var original = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(original); Assert.Equal(state, json.RootElement.GetProperty("state").GetString());
        Assert.Equal(ReviewEndpoints.ETag(s.Case.Id, 2), response.Headers.ETag!.Tag);
        await DecisionTestDatabase.Publisher().PublishAsync(s.Case.Id, 2, ReviewSamples.Request with { ProjectId = s.Case.ProjectId }, ReviewSamples.Result);
        using var replayRequest = Command(s, action, key); var replay = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal(original, await replay.Content.ReadAsStringAsync());
        Assert.Null(replay.Headers.ETag); Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
        var current = await client.GetAsync(Route(s)); Assert.Equal(ReviewEndpoints.ETag(s.Case.Id, 3), current.Headers.ETag!.Tag);
        using var conflictRequest = Command(s, action, key, reason: "different");
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(conflictRequest)).StatusCode);
        Assert.Equal(0, factory.Provider.Calls);
        Assert.DoesNotContain(factory.Logs.Messages, x => x.Contains("private-review-reason") || x.Contains(client.DefaultRequestHeaders.Authorization!.Parameter!));
    }

    [DecisionPostgresTheory]
    [InlineData("missing", 401)] [InlineData("expired", 401)] [InlineData("id-token", 401)]
    [InlineData("unknown", 403)] [InlineData("generator", 403)] [InlineData("inactive", 403)]
    [InlineData("service", 403)] [InlineData("cross-project", 403)]
    public async Task EveryRouteRequiresAuthenticatedHumanAndExplicitReviewer(string scenario, int status)
    {
        var s = await DecisionTestDatabase.Seed(); using var factory = new ReviewFactory(); using var client = Client(factory, s);
        if (scenario == "missing") client.DefaultRequestHeaders.Authorization = null;
        if (scenario == "expired") client.DefaultRequestHeaders.Authorization = new("Bearer", TestSecurity.Token(subject: s.Principal.Subject, expires: -60, notBefore: -120));
        if (scenario == "id-token") client.DefaultRequestHeaders.Authorization = new("Bearer", TestSecurity.Token(subject: s.Principal.Subject, type: "ID"));
        if (scenario == "unknown") client.DefaultRequestHeaders.Authorization = new("Bearer", TestSecurity.Token(subject: "unregistered"));
        if (scenario == "service") client.DefaultRequestHeaders.Authorization = new("Bearer", TestSecurity.Token(subject: s.Principal.Subject, kind: "Service", client: "factory-agent"));
        if (scenario is "generator" or "inactive")
        {
            await using var db = new ReviewDbContext(DecisionTestDatabase.Options("FACTORY_AUTH_OWNER_CONNECTION"));
            if (scenario == "inactive") await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE access."ProjectMemberships" SET "IsActive"=false WHERE "PrincipalId"={s.Principal.Id}""");
            else await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE access."ProjectMemberships" SET "Role"='Generator' WHERE "PrincipalId"={s.Principal.Id}""");
        }
        var route = scenario == "cross-project" ? Route(s).Replace(s.Case.ProjectId, "unknown") : Route(s);
        Assert.Equal(status, (int)(await client.GetAsync(route)).StatusCode);
        Assert.Equal(status, (int)(await client.GetAsync(route + "/artifacts/1")).StatusCode);
        using var request = Command(s); request.RequestUri = new(route, UriKind.Relative);
        Assert.Equal(status, (int)(await client.SendAsync(request)).StatusCode);
        Assert.Equal(0, factory.Provider.Calls); await DecisionTestDatabase.AssertCounts(s, 1, 0, 0);
    }

    [DecisionPostgresTheory]
    [InlineData("missing", 428)] [InlineData("weak", 400)] [InlineData("wildcard", 400)]
    [InlineData("list", 400)] [InlineData("other-case", 400)] [InlineData("leading-zero", 400)]
    [InlineData("stale", 412)] [InlineData("hash", 409)] [InlineData("version", 409)]
    [InlineData("key", 400)] [InlineData("duplicate-json", 400)] [InlineData("identity-field", 400)]
    [InlineData("reason", 400)] [InlineData("oversized", 400)] [InlineData("null-hash", 400)]
    public async Task PreconditionsAndBodyFailClosedWithoutWrites(string scenario, int status)
    {
        var s = await DecisionTestDatabase.Seed(); using var factory = new ReviewFactory(); using var client = Client(factory, s);
        using var request = Command(s, scenario == "reason" ? "reject" : "approve", reason: null,
            hash: scenario == "hash" ? new string('0',64) : null, version: scenario == "version" ? 2 : 1);
        var tag = ReviewEndpoints.ETag(s.Case.Id, 1);
        var replacement = scenario switch { "weak" => "W/" + tag, "wildcard" => "*", "list" => tag + ", " + tag,
            "other-case" => ReviewEndpoints.ETag(Guid.NewGuid(), 1), "leading-zero" => tag.Replace("-r1", "-r01"),
            "stale" => ReviewEndpoints.ETag(s.Case.Id, 2), _ => tag };
        request.Headers.Remove("If-Match"); if (scenario != "missing") request.Headers.TryAddWithoutValidation("If-Match", replacement);
        if (scenario == "key") { request.Headers.Remove("Idempotency-Key"); request.Headers.TryAddWithoutValidation("Idempotency-Key", "private-invalid-key"); }
        var body = await request.Content!.ReadAsStringAsync();
        if (scenario == "duplicate-json") body = body.Insert(1, "\"decision\":\"approve\",");
        if (scenario == "identity-field") body = body.Insert(1, "\"reviewerId\":\"forged\",");
        if (scenario == "oversized") body = new string('x', 17000);
        if (scenario == "null-hash") body = body.Replace("\"" + s.Artifact.ContentHash + "\"", "null");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request); Assert.Equal(status, (int)response.StatusCode);
        Assert.Null(response.Headers.ETag); await DecisionTestDatabase.AssertCounts(s, 1, 0, 0);
        Assert.Equal(0, factory.Provider.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task StorageFailuresHaveSafeCorrelatedDiagnostics(bool unexpected)
    {
        using var factory = new ReviewFactory(new BrokenStore(unexpected)); using var client = TestSecurity.Client(factory);
        var response = await client.GetAsync($"/api/projects/test/reviews/{Guid.NewGuid()}");
        Assert.Equal(unexpected ? 500 : 503, (int)response.StatusCode);
        var id = Assert.Single(response.Headers.GetValues("X-Request-Id"));
        Assert.Contains(factory.Logs.Messages, x => x.Contains(id));
        Assert.DoesNotContain("private-database-token-prompt", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(factory.Logs.Messages, x => x.Contains("private-database-token-prompt"));
    }

    [DecisionPostgresTheory]
    [MemberData(nameof(DecisionReasonCases.Http), MemberType = typeof(DecisionReasonCases))]
    public async Task HttpReasonPolicyMatchesDomainAndDatabase(string label, ReviewDecision action, string? reason, bool valid)
    {
        Assert.True(DecisionReasonPolicy.IsValid(action,reason) == valid,label);
        var seed = await DecisionTestDatabase.Seed(); using var factory = new ReviewFactory(); using var client = Client(factory,seed);
        var name = action switch { ReviewDecision.Approve => "approve", ReviewDecision.Reject => "reject", _ => "requestChanges" };
        using var request = Command(seed,name,reason: reason);
        var response = await client.SendAsync(request);
        Assert.Equal(valid ? HttpStatusCode.OK : HttpStatusCode.BadRequest,response.StatusCode);
        await DecisionTestDatabase.AssertCounts(seed,valid ? 2 : 1,valid ? 1 : 0,valid ? 1 : 0);
        Assert.Equal(0,factory.Provider.Calls);
    }

    [Theory]
    [InlineData("23503",false)] [InlineData("23503",true)]
    [InlineData("23514",false)] [InlineData("23514",true)]
    [InlineData("23502",true)] [InlineData("23505",true)] [InlineData("08006",true)]
    public async Task WrappedIntegrityFailuresAreNotStorageUnavailable(string sqlState, bool wrapped)
    {
        const string privateData = "private-sql-secret-artifact-payload";
        Exception failure = new Npgsql.PostgresException(privateData,"ERROR","ERROR",sqlState,detail: privateData);
        if (wrapped) failure = new DbUpdateException(privateData,failure);
        using var factory = new ReviewFactory(new BrokenStore(false,failure)); using var client = TestSecurity.Client(factory);
        var response = await client.GetAsync($"/api/projects/test/reviews/{Guid.NewGuid()}");
        var integrity = sqlState.StartsWith("23",StringComparison.Ordinal);
        Assert.Equal(integrity ? 500 : 503,(int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(integrity ? "review_integrity_failure" : "review_storage_unavailable",body);
        Assert.DoesNotContain(privateData,body);
        Assert.DoesNotContain(factory.Logs.Messages,x => x.Contains(privateData) || x.Contains(sqlState));
        Assert.Contains(factory.Logs.Messages,x => x.Contains(Assert.Single(response.Headers.GetValues("X-Request-Id"))));
    }

    private sealed class BrokenStore(bool unexpected, Exception? failure = null) : IReviewDecisionStore
    {
        public Task<ReviewSnapshot> GetAsync(Factory.Core.Authorization.TrustedIdentity identity, string projectId, Guid caseId, CancellationToken cancellationToken = default)
            => throw (failure ?? (unexpected ? new InvalidOperationException("private-database-token-prompt") : (Exception)new Npgsql.NpgsqlException("private-database-token-prompt")));
        public Task<ReviewArtifact> GetArtifactAsync(Factory.Core.Authorization.TrustedIdentity identity, string projectId, Guid caseId, int version, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DecisionOutcome> DecideAsync(Factory.Core.Authorization.TrustedIdentity identity, string projectId, Guid caseId, DecisionCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
    private sealed class ReviewFactory(IReviewDecisionStore? store = null) : WebApplicationFactory<Program>
    {
        internal AuthenticationTests.CountingProvider Provider { get; } = new();
        internal AuthenticationTests.SafeLogs Logs { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); builder.UseSetting("OpenAI:ApiKey", "synthetic-test-credential");
            TestSecurity.Configure(builder);
            builder.ConfigureLogging(x => x.ClearProviders().AddProvider(Logs));
            builder.ConfigureServices(services => {
                services.RemoveAll<IModelProvider>(); services.AddSingleton<IModelProvider>(Provider);
                services.RemoveAll<IReviewDecisionStore>(); services.AddSingleton(store ?? DecisionTestDatabase.Store());
            });
        }
    }
}
