using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Factory.Core.Authorization;
using Factory.Core.Orchestration;
using Factory.Core.Tenancy;
using Factory.Tests.Integration;
using Factory.Tests.Lifecycle;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Factory.Tests.Orchestration;

[Collection("Lifecycle database")]
public class WorkspaceListingTests
{
    [OrchestrationFact]
    public async Task ProjectsAreListedOnlyForTheirOwnMembersWithDecisionAuthority()
    {
        await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        await using var service = await LifecycleFixture.Create(PrincipalKind.Service);
        var mine = Assert.Single(await Durable.Api.GetMyProjectsAsync(f.Actor));
        Assert.Equal((f.Project, f.Organization, true), (mine.ProjectId, mine.OrganizationId, mine.CanDecide));
        Assert.Equal(["Generator", "Reviewer"], mine.Roles);
        Assert.DoesNotContain(await Durable.Api.GetMyProjectsAsync(other.Actor), p => p.ProjectId == f.Project);
        // A service identity can generate but never decides.
        var generated = Assert.Single(await Durable.Api.GetMyProjectsAsync(service.Actor));
        Assert.Equal((service.Project, false), (generated.ProjectId, generated.CanDecide));
        Assert.Equal(["Generator"], generated.Roles);
        Assert.Empty(await Durable.Api.GetMyProjectsAsync(new TrustedIdentity(f.Actor.Issuer, "unknown-" + Guid.NewGuid().ToString("N"), PrincipalKind.Human)));
        // A deactivated membership disappears from the list.
        await Durable.Sql("""UPDATE access."ProjectMemberships" SET "IsActive"=false,"Revision"="Revision"+1 WHERE "ProjectId"=@p""",
            new NpgsqlParameter("p", other.Project));
        Assert.Empty(await Durable.Api.GetMyProjectsAsync(other.Actor));
    }

    [OrchestrationFact]
    public async Task RunsAreListedNewestFirstAsSafeSummariesForProjectReadersOnly()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var first = await Durable.Submit(f);
        var second = await Durable.Api.SubmitAsync(f.Actor, Guid.NewGuid(), Durable.Request(f), WorkflowVersions.Decomposition);
        var runs = await Durable.Api.ListRunsAsync(f.Actor, f.Project);
        Assert.Equal([second.RunId, first.RunId], runs.Select(r => r.RunId));
        Assert.Equal([WorkflowVersions.Decomposition, WorkflowVersions.AnalysisArchitecture], runs.Select(r => r.WorkflowVersion));
        Assert.All(runs, r => Assert.Equal((RunStatus.Queued, 0), (r.Status, r.PendingDecisions)));
        Assert.Equal(OrchestrationError.Forbidden, (await Assert.ThrowsAsync<OrchestrationException>(() => Durable.Api.ListRunsAsync(other.Actor, f.Project))).Error);
        Assert.Empty(await Durable.Api.ListRunsAsync(other.Actor, other.Project));
        await using var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")); await db.OpenAsync();
        foreach (var sql in new[] { "SELECT orchestration.list_runs('p','i','s','Human')", "SELECT tenancy.my_projects('i','s','Human')" })
        {
            await using var command = new NpgsqlCommand(sql, db);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync())).SqlState);
        }
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task HttpServesTheCallersProjectsAndRuns()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenAI:ApiKey", "sk-test-placeholder-not-a-real-key-1234567890");
            TestSecurity.Configure(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkflowRunStore>(); services.AddSingleton<IWorkflowRunStore>(Durable.Api);
                services.RemoveAll<IWorkspaceDirectory>(); services.AddSingleton<IWorkspaceDirectory>(Durable.Api);
            });
        });
        using (var anonymous = factory.CreateClient()) using (var denied = await anonymous.GetAsync("/api/me/projects"))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestSecurity.Token(subject: f.Actor.Subject));
        var run = await Durable.Submit(f);
        using var projects = await client.GetAsync("/api/me/projects");
        Assert.Equal(HttpStatusCode.OK, projects.StatusCode);
        Assert.Equal("no-store", projects.Headers.CacheControl!.ToString());
        var project = Assert.Single(JsonDocument.Parse(await projects.Content.ReadAsStringAsync()).RootElement.EnumerateArray());
        Assert.Equal((f.Project, true), (project.GetProperty("projectId").GetString(), project.GetProperty("canDecide").GetBoolean()));
        using var runs = await client.GetAsync($"/api/projects/{f.Project}/runs");
        var listed = Assert.Single(JsonDocument.Parse(await runs.Content.ReadAsStringAsync()).RootElement.EnumerateArray());
        Assert.Equal((run.RunId, "queued"), (listed.GetProperty("runId").GetGuid(), listed.GetProperty("status").GetString()));
        using (var foreign = await client.GetAsync("/api/projects/another-project/runs")) Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        // Workspace (factory-web) tokens are accepted only when that client is configured, and only as humans;
        // other token profiles are rejected at authentication (401).
        using (var unconfigured = new HttpRequestMessage(HttpMethod.Get, "/api/me/projects"))
        {
            unconfigured.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestSecurity.Token(subject: f.Actor.Subject, client: "factory-web"));
            using var refused = await client.SendAsync(unconfigured);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        using var webFactory = factory.WithWebHostBuilder(builder => builder.UseSetting("Authentication:WebClient", "factory-web"));
        using var web = webFactory.CreateClient();
        web.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestSecurity.Token(subject: f.Actor.Subject, client: "factory-web"));
        using (var accepted = await web.GetAsync("/api/me/projects")) Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using (var forgedService = new HttpRequestMessage(HttpMethod.Get, "/api/me/projects"))
        {
            forgedService.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                TestSecurity.Token(subject: f.Actor.Subject, client: "factory-web", kind: "Service"));
            using var refused = await web.SendAsync(forgedService);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        await Durable.Drain();
    }

    [Fact]
    public void AWebClientMustDifferFromTheOtherClientsAndTheAudience()
    {
        foreach (var web in new[] { "", "factory-dev-cli", "factory-agent", "factory-api" })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Authority"] = "https://issuer.example/realms/x", ["Authentication:Audience"] = "factory-api",
                ["Authentication:HumanClient"] = "factory-dev-cli", ["Authentication:ServiceClient"] = "factory-agent", ["Authentication:WebClient"] = web
            }).Build();
            Assert.Throws<InvalidOperationException>(() => Factory.Api.Security.FactoryAuthSettings.Read(configuration,
                new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" }));
        }
    }
}
