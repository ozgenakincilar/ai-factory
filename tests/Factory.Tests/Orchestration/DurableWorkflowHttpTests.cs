using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Factory.Core.Lifecycle;
using Factory.Core.Orchestration;
using Factory.Tests.Integration;
using Factory.Tests.Lifecycle;
using Factory.Tests.Reviews;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Factory.Tests.Orchestration;

[Collection("Lifecycle database")]
public class DurableWorkflowHttpTests
{
    [OrchestrationFact]
    public async Task SubmissionReturnsAcceptedImmediatelyReplaysAndNeverExecutesInTheApi()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        using var factory = Factory(); using var client = Client(factory, f); var key = Guid.NewGuid();
        using var first = await client.SendAsync(Submit(f.Project, key));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl!.ToString());
        using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var run = body.RootElement.GetProperty("runId").GetGuid();
        Assert.Equal("queued", body.RootElement.GetProperty("status").GetString());
        Assert.Equal($"/api/projects/{f.Project}/runs/{run:D}", first.Headers.Location!.OriginalString);
        using var replay = await client.SendAsync(Submit(f.Project, key));
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        using var status = await client.GetAsync($"/api/projects/{f.Project}/runs/{run:D}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var text = await status.Content.ReadAsStringAsync();
        // Status is safe metadata: no task, context, model output or lease material.
        // Usage counters (inputTokens/outputTokens) are expected; lease material is not.
        Assert.DoesNotContain(ReviewSamples.Request.Task, text);
        Assert.DoesNotContain("tokenhash", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("leasetoken", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attemptId", text, StringComparison.OrdinalIgnoreCase);
        using var view = JsonDocument.Parse(text);
        Assert.Equal("queued", view.RootElement.GetProperty("status").GetString());
        Assert.All(view.RootElement.GetProperty("steps").EnumerateArray(), s => Assert.Equal("pending", s.GetProperty("status").GetString()));
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task HttpRejectsAnonymousCrossProjectMalformedAndMissingKeys()
    {
        await using var f = await LifecycleFixture.Create(); using var factory = Factory(); using var client = Client(factory, f);
        using (var anonymous = factory.CreateClient()) using (var response = await anonymous.SendAsync(Submit(f.Project, Guid.NewGuid())))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var denied = await client.SendAsync(Submit("another-project", Guid.NewGuid()))) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var malformed = await client.SendAsync(Submit(f.Project, Guid.NewGuid(), "{\"task\":\"x\",\"task\":\"y\"}")))
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using (var request = Submit(f.Project, Guid.NewGuid()))
        {
            request.Headers.Remove("Idempotency-Key");
            using var missing = await client.SendAsync(request); Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        }
        using (var unknown = await client.GetAsync($"/api/projects/{f.Project}/runs/{Guid.NewGuid():D}")) Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [OrchestrationFact]
    public async Task HumanDecisionAndCancellationEndpointsUseStrictBodiesAndSafeViews()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        using var factory = Factory(); using var client = Client(factory, f);
        var run = await Durable.Submit(f); var token = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var step = (await Durable.Worker.ClaimAsync("worker", token))!;
        var decision = await Durable.Worker.RequestDecisionAsync(step.AttemptId, token, "customer_model_insufficient", ["use_factory_model", "stop"]);
        HttpRequestMessage Decide(string body) => new(HttpMethod.Post, $"/api/projects/{f.Project}/runs/{run.RunId:D}/decisions/{decision:D}")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using (var waiting = await client.GetAsync($"/api/projects/{f.Project}/runs/{run.RunId:D}"))
        {
            using var json = JsonDocument.Parse(await waiting.Content.ReadAsStringAsync());
            Assert.Equal("waiting", json.RootElement.GetProperty("status").GetString());
            var pending = json.RootElement.GetProperty("decisions")[0];
            Assert.Equal(("pending", "customer_model_insufficient"), (pending.GetProperty("status").GetString(), pending.GetProperty("reason").GetString()));
        }
        foreach (var bad in new[] { """{"choice":"use_factory_model","extra":1}""", """{"choice":"Use Factory"}""", "[]", """{"choice":"retry_later"}""" })
        {
            using var request = Decide(bad); using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var request = Decide("""{"choice":"use_factory_model"}""")) using (var decided = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
            using var json = JsonDocument.Parse(await decided.Content.ReadAsStringAsync());
            Assert.Equal("running", json.RootElement.GetProperty("status").GetString());
        }
        using (var cancel = await client.PostAsync($"/api/projects/{f.Project}/runs/{run.RunId:D}/cancel", null))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            using var json = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync());
            Assert.Equal("cancelled", json.RootElement.GetProperty("status").GetString());
        }
        using (var anonymous = factory.CreateClient()) using (var response = await anonymous.PostAsync($"/api/projects/{f.Project}/runs/{run.RunId:D}/cancel", null))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [OrchestrationFact]
    public async Task UsageEndpointReturnsTotalsAndRejectsInvalidRanges()
    {
        await using var f = await LifecycleFixture.Create(); using var factory = Factory(); using var client = Client(factory, f);
        using (var usage = await client.GetAsync($"/api/projects/{f.Project}/usage"))
        {
            Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
            using var json = JsonDocument.Parse(await usage.Content.ReadAsStringAsync());
            Assert.Equal(0, json.RootElement.GetProperty("totals").GetProperty("calls").GetInt32());
            Assert.Equal(f.Project, json.RootElement.GetProperty("projectId").GetString());
        }
        using (var inverted = await client.GetAsync($"/api/projects/{f.Project}/usage?from=2026-10-02T00:00:00Z&to=2026-10-01T00:00:00Z"))
            Assert.Equal(HttpStatusCode.BadRequest, inverted.StatusCode);
        using (var denied = await client.GetAsync("/api/projects/another-project/usage")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var organization = await client.GetAsync($"/api/organizations/{f.Organization}/usage"))
        {
            Assert.Equal(HttpStatusCode.OK, organization.StatusCode);
            using var json = JsonDocument.Parse(await organization.Content.ReadAsStringAsync());
            Assert.Equal(f.Organization, json.RootElement.GetProperty("organizationId").GetString());
        }
        using (var foreign = await client.GetAsync("/api/organizations/another-organization/usage")) Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    private static WebApplicationFactory<Program> Factory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenAI:ApiKey", "sk-test-placeholder-not-a-real-key-1234567890");
            TestSecurity.Configure(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkflowRunStore>(); services.AddSingleton<IWorkflowRunStore>(Durable.Api);
                services.RemoveAll<Factory.Core.Tenancy.ITenantUsageStore>(); services.AddSingleton<Factory.Core.Tenancy.ITenantUsageStore>(Durable.Api);
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> factory, LifecycleFixture f)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestSecurity.Token(subject: f.Actor.Subject));
        return client;
    }

    private static HttpRequestMessage Submit(string project, Guid key, string? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/runs");
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        request.Content = new StringContent(body ?? JsonSerializer.Serialize(new
        {
            task = ReviewSamples.Request.Task, context = ReviewSamples.Request.Context,
            confirmedRequirements = ReviewSamples.Request.ConfirmedRequirements, workflowVersion = LifecycleLimits.WorkflowVersion
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
        return request;
    }
}
