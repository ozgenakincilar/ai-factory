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
        Assert.DoesNotContain(ReviewSamples.Request.Task, text); Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
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

    private static WebApplicationFactory<Program> Factory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenAI:ApiKey", "sk-test-placeholder-not-a-real-key-1234567890");
            TestSecurity.Configure(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkflowRunStore>(); services.AddSingleton<IWorkflowRunStore>(Durable.Api);
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
