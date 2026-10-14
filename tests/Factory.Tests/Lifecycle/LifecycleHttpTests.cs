using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Factory.Core.Architecture;
using Factory.Core.Lifecycle;
using Factory.Core.Models;
using Factory.Core.Reviews;
using Factory.Infrastructure.Lifecycle;
using Factory.Infrastructure.Reviews;
using Factory.Orchestration.Lifecycle;
using Factory.Tests.Integration;
using Factory.Tests.Reviews;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Factory.Tests.Lifecycle;

[Collection("Lifecycle database")]
public class LifecycleHttpTests
{
    [LifecycleFact]
    public async Task RevisionPostLostResponseReplayAndStalePreconditionPreserveHistory()
    {
        await using var f = await LifecycleFixture.Create(); var (_, _, initial) = await f.Publish();
        var provider = new Provider(); using var factory = Factory(f, provider); using var client = Client(factory, f);
        using var current = await client.GetAsync($"/api/projects/{f.Project}/work-items/{initial.WorkItemId}");
        var tag = current.Headers.ETag!.Tag; var key = Guid.NewGuid();
        HttpRequestMessage Revise(Guid id)
        {
            var request = Create(f.Project, id);
            request.RequestUri = new Uri($"/api/projects/{f.Project}/work-items/{initial.WorkItemId}/analysis-architecture", UriKind.Relative);
            request.Headers.TryAddWithoutValidation("If-Match", tag); return request;
        }
        // Deliberately discard the committed HTTP response body; reconcile using a new client.
        using (var request = Revise(key)) using (var lost = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.OK, lost.StatusCode);
        using var reconnect = Client(factory, f);
        using var replayRequest = Revise(key); using var replay = await reconnect.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Null(replay.Headers.ETag);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        using var result = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(2, result.RootElement.GetProperty("publication").GetProperty("version").GetInt32());
        Assert.Equal(initial.Publication!.CaseId, result.RootElement.GetProperty("publication").GetProperty("caseId").GetGuid());
        using var staleRequest = Revise(Guid.NewGuid()); using var stale = await client.SendAsync(staleRequest);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode); Assert.Equal(2, provider.Calls);
        using var updated = await client.GetAsync($"/api/projects/{f.Project}/work-items/{initial.WorkItemId}");
        Assert.NotEqual(tag, updated.Headers.ETag!.Tag);
        var original = await f.Decisions.GetArtifactAsync(f.Actor, f.Project, initial.Publication.CaseId, 1);
        Assert.Equal(initial.Publication.ContentHash, original.ContentHash);
    }

    private static WebApplicationFactory<Program> Factory(LifecycleFixture f, IModelProvider provider)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder=>
        {
            builder.UseSetting("OpenAI:ApiKey","sk-test-placeholder-not-a-real-key-1234567890");
            TestSecurity.Configure(builder);
            builder.ConfigureServices(services=>
            {
                services.RemoveAll<ILifecycleStore>(); services.AddSingleton<ILifecycleStore>(f.Store);
                services.RemoveAll<IReviewStore>(); services.AddSingleton<IReviewStore>(new PostgresReviewStore(LifecycleFixture.Options(LifecycleFixture.Runtime)));
                services.RemoveAll<LifecycleService>(); services.AddSingleton(sp=>new LifecycleService(f.Store,sp.GetRequiredService<IReviewStore>(),provider));
                services.RemoveAll<IReviewDecisionStore>(); services.AddSingleton<IReviewDecisionStore>(f.Decisions);
            });
        });
    private static HttpClient Client(WebApplicationFactory<Program> factory,LifecycleFixture f)
    {
        var client=factory.CreateClient();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",TestSecurity.Token(subject:f.Actor.Subject));
        return client;
    }
    private static HttpRequestMessage Create(string project,Guid key,string? body=null)
    {
        var request=new HttpRequestMessage(HttpMethod.Post,$"/api/projects/{project}/work-items");
        request.Headers.Add("Idempotency-Key",key.ToString("D"));
        request.Content=new StringContent(body ?? JsonSerializer.Serialize(new { task=ReviewSamples.Request.Task,context=ReviewSamples.Request.Context,
            confirmedRequirements=ReviewSamples.Request.ConfirmedRequirements,workflowVersion=LifecycleLimits.WorkflowVersion },new JsonSerializerOptions(JsonSerializerDefaults.Web)),Encoding.UTF8,"application/json");
        return request;
    }
    private sealed class Provider : IModelProvider
    {
        internal int Calls;
        internal TaskCompletionSource? Started;
        internal TaskCompletionSource? Release;
        public async Task<ModelResponse> GenerateAsync(ModelRequest request,CancellationToken cancellationToken=default)
        {
            var call=Interlocked.Increment(ref Calls);
            if(call==1 && Started is not null && Release is not null){Started.TrySetResult();await Release.Task.WaitAsync(cancellationToken);}
            return new ModelResponse(call%2==1?JsonSerializer.Serialize(ReviewSamples.Result.Analysis,ArchitectureJson.Options):JsonSerializer.Serialize(ReviewSamples.Result.Architecture,ArchitectureJson.Options),"test");
        }
    }

    [LifecycleFact]
    public async Task AuthenticatedCreateReplayAndReviewUseExactPersistedRun()
    {
        await using var f=await LifecycleFixture.Create();var provider=new Provider();using var factory=Factory(f,provider);using var client=Client(factory,f);
        var key=Guid.NewGuid();using var first=await client.SendAsync(Create(f.Project,key));
        Assert.Equal(HttpStatusCode.Created,first.StatusCode);
        var original=await first.Content.ReadAsStringAsync();using var json=JsonDocument.Parse(original);
        var run=json.RootElement.GetProperty("runId").GetGuid();var work=json.RootElement.GetProperty("workItemId").GetGuid();
        using var replay=await client.SendAsync(Create(f.Project,key));
        Assert.Equal(original,await replay.Content.ReadAsStringAsync());Assert.Equal(2,provider.Calls);
        Assert.Equal("true",replay.Headers.GetValues("Idempotency-Replayed").Single());Assert.Null(replay.Headers.ETag);
        using var current=await client.GetAsync($"/api/projects/{f.Project}/work-items/{work}");Assert.Equal(HttpStatusCode.OK,current.StatusCode);
        Assert.NotNull(current.Headers.ETag);
        var metadata=await current.Content.ReadAsStringAsync();Assert.DoesNotContain("analysis",metadata,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("reason",metadata,StringComparison.OrdinalIgnoreCase);
        using var status=await client.GetAsync($"/api/projects/{f.Project}/workflow-runs/{run}");Assert.Equal(HttpStatusCode.OK,status.StatusCode);
        var caseId=json.RootElement.GetProperty("publication").GetProperty("caseId").GetGuid();
        using var artifact=await client.GetAsync($"/api/projects/{f.Project}/reviews/{caseId}/artifacts/1");Assert.Equal(HttpStatusCode.OK,artifact.StatusCode);
        using var stored=JsonDocument.Parse(await artifact.Content.ReadAsStringAsync());Assert.Equal(run,stored.RootElement.GetProperty("workflowId").GetGuid());
    }

    [LifecycleFact]
    public async Task ConcurrentHttpReplayDoesNotInvokeAnotherWorkflow()
    {
        await using var f=await LifecycleFixture.Create();var provider=new Provider{Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        using var factory=Factory(f,provider);using var client=Client(factory,f);var key=Guid.NewGuid();
        var first=client.SendAsync(Create(f.Project,key));await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var second=await client.SendAsync(Create(f.Project,key));Assert.Equal(HttpStatusCode.Accepted,second.StatusCode);Assert.Equal(1,provider.Calls);
            using var secondJson=JsonDocument.Parse(await second.Content.ReadAsStringAsync());provider.Release.SetResult();
            using var completed=await first;using var firstJson=JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
            Assert.Equal(secondJson.RootElement.GetProperty("runId").GetGuid(),firstJson.RootElement.GetProperty("runId").GetGuid());Assert.Equal(2,provider.Calls);
        }
        finally{provider.Release.TrySetResult();}
    }

    [LifecycleFact]
    public async Task HttpRejectsAnonymousCrossProjectDuplicateFieldsAndStrongTagViolations()
    {
        await using var f=await LifecycleFixture.Create();var provider=new Provider();using var factory=Factory(f,provider);using var client=Client(factory,f);
        using(var denied=await client.SendAsync(Create("another-project",Guid.NewGuid())))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        using(var malformed=await client.SendAsync(Create(f.Project,Guid.NewGuid(),"{\"task\":\"x\",\"task\":\"y\"}")))Assert.Equal(HttpStatusCode.BadRequest,malformed.StatusCode);
        using(var anonymous=factory.CreateClient())using(var response=await anonymous.SendAsync(Create(f.Project,Guid.NewGuid())))Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
        var command=f.Command;var admission=await f.Store.AdmitAsync(f.Actor,command,f.Token);await f.Store.FinishAsync(f.Actor,f.Project,admission.Outcome.RunId,f.Token,GenerationStatus.Cancelled,"generation_cancelled");
        foreach(var tag in new string?[]{null,"*","W/\"bad\""})
        {
            using var request=Create(f.Project,Guid.NewGuid());request.RequestUri=new Uri($"/api/projects/{f.Project}/work-items/{admission.Outcome.WorkItemId}/analysis-architecture",UriKind.Relative);
            if(tag is not null)request.Headers.TryAddWithoutValidation("If-Match",tag);
            using var response=await client.SendAsync(request);Assert.Equal(tag is null?428:400,(int)response.StatusCode);
        }
        Assert.Equal(0,provider.Calls);
    }
}
