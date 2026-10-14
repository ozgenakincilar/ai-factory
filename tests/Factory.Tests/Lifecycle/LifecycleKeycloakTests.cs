using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Factory.Core.Architecture;
using Factory.Core.Authorization;
using Factory.Core.Lifecycle;
using Factory.Core.Models;
using Factory.Core.Reviews;
using Factory.Infrastructure.Reviews;
using Factory.Orchestration.Lifecycle;
using Factory.Tests.Authorization;
using Factory.Tests.Reviews;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Factory.Tests.Lifecycle;

public sealed class LifecycleKeycloakFactAttribute : FactAttribute
{
    public LifecycleKeycloakFactAttribute() => Skip = new LifecycleFactAttribute().Skip ?? new LiveKeycloakFactAttribute().Skip;
}

[Collection("Lifecycle database")]
public class LifecycleKeycloakTests
{
    private const string Issuer = "http://127.0.0.1:58080/realms/factory-dev";
    [LifecycleKeycloakFact]
    public async Task LiveHumanPublishesAndApproves_ServicePublishesButCannotReadReviewOrApprove()
    {
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("FACTORY_KEYCLOAK_HUMAN_TOKENS")!));
        var humanToken = evidence.RootElement.GetProperty("accessToken").GetString()!;
        await using var human = await LifecycleFixture.Create(issuer: Issuer, subject: new JwtSecurityTokenHandler().ReadJwtToken(humanToken).Subject);
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("FACTORY_KEYCLOAK_SETTINGS")!));
        using var tokenClient = new HttpClient();
        using var tokenResponse = await tokenClient.PostAsync(Issuer + "/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "client_credentials", ["client_id"] = "factory-agent", ["client_secret"] = settings.RootElement.GetProperty("ServiceSecret").GetString()! }));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        using var tokens = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync()); var serviceToken = tokens.RootElement.GetProperty("access_token").GetString()!;
        await using var service = await LifecycleFixture.Create(PrincipalKind.Service, Issuer, new JwtSecurityTokenHandler().ReadJwtToken(serviceToken).Subject);
        var provider = new Provider();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); builder.UseSetting("OpenAI:ApiKey", "synthetic-test-credential");
            builder.UseSetting("Authentication:Authority", Issuer); builder.UseSetting("Authentication:Audience", "factory-api");
            builder.UseSetting("Authentication:HumanClient", "factory-dev-cli"); builder.UseSetting("Authentication:ServiceClient", "factory-agent");
            builder.UseSetting("Authentication:AllowDevelopmentLoopbackHttp", "true");
            builder.UseSetting("ConnectionStrings:Authorization", Environment.GetEnvironmentVariable("FACTORY_AUTH_READER_CONNECTION"));
            builder.UseSetting("ConnectionStrings:Reviews", Environment.GetEnvironmentVariable("FACTORY_DECISION_TEST_CONNECTION"));
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILifecycleStore>(); services.AddSingleton<ILifecycleStore>(human.Store);
                services.RemoveAll<IReviewStore>(); services.AddSingleton<IReviewStore>(new PostgresReviewStore(LifecycleFixture.Options(LifecycleFixture.Runtime)));
                services.RemoveAll<LifecycleService>(); services.AddSingleton(sp => new LifecycleService(human.Store, sp.GetRequiredService<IReviewStore>(), provider));
                services.RemoveAll<IReviewDecisionStore>(); services.AddSingleton<IReviewDecisionStore>(human.Decisions);
                services.AddSingleton<IStartupFilter, LoopbackTransport>();
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", humanToken);
        var humanCase = await Generate(human.Project);
        using var review = await client.GetAsync($"/api/projects/{human.Project}/reviews/{humanCase.CaseId}"); Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        using var approve = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{human.Project}/reviews/{humanCase.CaseId}");
        approve.Headers.TryAddWithoutValidation("If-Match", review.Headers.ETag!.Tag); approve.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        approve.Content = JsonContent.Create(new { decision = "approve", artifactVersion = 1, artifactSha256 = humanCase.ContentHash, reason = (string?)null });
        using var approved = await client.SendAsync(approve); Assert.Equal(HttpStatusCode.OK, approved.StatusCode); Assert.Equal(2, provider.Calls);
        client.DefaultRequestHeaders.Authorization = new("Bearer", serviceToken);
        var serviceCase = await Generate(service.Project);
        using var forbiddenRead = await client.GetAsync($"/api/projects/{service.Project}/reviews/{serviceCase.CaseId}/artifacts/1"); Assert.Equal(HttpStatusCode.Forbidden, forbiddenRead.StatusCode);
        using var forbiddenDecision = await client.PostAsJsonAsync($"/api/projects/{service.Project}/reviews/{serviceCase.CaseId}", new { }); Assert.Equal(HttpStatusCode.Forbidden, forbiddenDecision.StatusCode);
        Assert.Equal(4, provider.Calls);

        async Task<PublicationReference> Generate(string project)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/work-items"); request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            request.Content = JsonContent.Create(new { task = ReviewSamples.Request.Task, context = ReviewSamples.Request.Context, confirmedRequirements = ReviewSamples.Request.ConfirmedRequirements, workflowVersion = LifecycleLimits.WorkflowVersion });
            using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var p = json.RootElement.GetProperty("publication");
            return new(p.GetProperty("caseId").GetGuid(), p.GetProperty("version").GetInt32(), p.GetProperty("revision").GetInt64(), p.GetProperty("contentHash").GetString()!);
        }
    }
    private sealed class Provider : IModelProvider
    {
        internal int Calls;
        public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref Calls);
            return Task.FromResult(new ModelResponse(call % 2 == 1 ? JsonSerializer.Serialize(ReviewSamples.Result.Analysis, ArchitectureJson.Options)
                : JsonSerializer.Serialize(ReviewSamples.Result.Architecture, ArchitectureJson.Options), "test"));
        }
    }
    private sealed class LoopbackTransport : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        { app.Use((context, continuation) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return continuation(context); }); next(app); };
    }
}
