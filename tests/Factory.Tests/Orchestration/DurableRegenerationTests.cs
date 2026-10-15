using System.Net;
using System.Text;
using System.Text.Json;
using Factory.Core.Lifecycle;
using Factory.Core.Orchestration;
using Factory.Core.Reviews;
using Factory.Infrastructure.Reviews;
using Factory.Orchestration.Durable;
using Factory.Tests.Integration;
using Factory.Tests.Lifecycle;
using Factory.Tests.Reviews;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Factory.Tests.Orchestration;

[Collection("Lifecycle database")]
public class DurableRegenerationTests
{
    private static async Task<RunView> Complete(LifecycleFixture f, Guid run)
    {
        var processor = new DurableWorkflowProcessor(Durable.Worker, new Durable.Provider());
        for (var i = 0; i < 3; i++) await processor.ProcessNextAsync("worker", default);
        return await Durable.Api.GetRunAsync(f.Actor, f.Project, run);
    }

    private static async Task<RunPublication> Published(LifecycleFixture f)
        => (await Complete(f, (await Durable.Submit(f)).RunId)).Publication!;

    [OrchestrationFact]
    public async Task RegenerationPublishesTheNextVersionOfTheSameCaseAfterADecision()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var first = await Published(f);
        var v1 = await f.Decisions.GetArtifactAsync(f.Actor, f.Project, first.CaseId, 1);
        await f.Decisions.DecideAsync(f.Actor, f.Project, first.CaseId, new DecisionCommand(1, 1, v1.ContentHash, ReviewDecision.Approve, null, Guid.NewGuid()));
        var work = await f.Store.GetWorkItemAsync(f.Actor, f.Project, first.WorkItemId);
        var key = Guid.NewGuid();
        var submitted = await Durable.Api.SubmitRegenerationAsync(f.Actor, key, work.WorkItemId, work.ETag, Durable.Request(f));
        Assert.True((await Durable.Api.SubmitRegenerationAsync(f.Actor, key, work.WorkItemId, work.ETag, Durable.Request(f))).Replayed);
        Assert.Equal(OrchestrationError.Conflict, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.SubmitRegenerationAsync(f.Actor, key, work.WorkItemId, work.ETag, Durable.Request(f) with { Context = "changed" }))).Error);
        var view = await Complete(f, submitted.RunId);
        Assert.Equal(RunStatus.Succeeded, view.Status);
        Assert.Equal((first.WorkItemId, first.CaseId, 2), (view.Publication!.WorkItemId, view.Publication.CaseId, view.Publication.Version));
        var current = await f.Store.GetWorkItemAsync(f.Actor, f.Project, first.WorkItemId);
        Assert.Equal((submitted.RunId, ReviewState.PendingReview, 2), (current.LatestRunId, current.ReviewState!.Value, current.ArtifactVersion!.Value));
        Assert.Equal(submitted.RunId, ReviewArtifactSerializer.Read(await f.Decisions.GetArtifactAsync(f.Actor, f.Project, first.CaseId, 2)).WorkflowId);
        Assert.Equal(v1.GetContent(), (await f.Decisions.GetArtifactAsync(f.Actor, f.Project, first.CaseId, 1)).GetContent());
        var m6 = await f.Store.GetRunAsync(f.Actor, f.Project, submitted.RunId);
        Assert.Equal((GenerationStatus.Published, 200), (m6.Status, m6.HttpStatus));
    }

    [OrchestrationFact]
    public async Task StaleETagsAndConcurrentRegenerationsAreRejectedAtSubmission()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var first = await Published(f);
        var before = await f.Store.GetWorkItemAsync(f.Actor, f.Project, first.WorkItemId);
        var v1 = await f.Decisions.GetArtifactAsync(f.Actor, f.Project, first.CaseId, 1);
        await f.Decisions.DecideAsync(f.Actor, f.Project, first.CaseId, new DecisionCommand(1, 1, v1.ContentHash, ReviewDecision.RequestChanges, "more detail", Guid.NewGuid()));
        Assert.Equal(OrchestrationError.StaleRevision, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.SubmitRegenerationAsync(f.Actor, Guid.NewGuid(), first.WorkItemId, before.ETag, Durable.Request(f)))).Error);
        var now = await f.Store.GetWorkItemAsync(f.Actor, f.Project, first.WorkItemId);
        await Durable.Api.SubmitRegenerationAsync(f.Actor, Guid.NewGuid(), first.WorkItemId, now.ETag, Durable.Request(f));
        Assert.Equal(OrchestrationError.Conflict, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.SubmitRegenerationAsync(f.Actor, Guid.NewGuid(), first.WorkItemId, now.ETag, Durable.Request(f)))).Error);
        var missing = Guid.NewGuid();
        Assert.Equal(OrchestrationError.NotFound, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.SubmitRegenerationAsync(f.Actor, Guid.NewGuid(), missing, WorkItemETag.Format(missing, 1, null, 0), Durable.Request(f)))).Error);
        // An ETag that belongs to another work item is malformed for this one.
        Assert.Equal(OrchestrationError.InvalidCommand, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.SubmitRegenerationAsync(f.Actor, Guid.NewGuid(), first.WorkItemId, WorkItemETag.Format(missing, 1, null, 0), Durable.Request(f)))).Error);
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task ADecisionAfterSubmissionMakesPublicationStaleInsteadOfOverwriting()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var first = await Published(f);
        var work = await f.Store.GetWorkItemAsync(f.Actor, f.Project, first.WorkItemId);
        var run = await Durable.Api.SubmitRegenerationAsync(f.Actor, Guid.NewGuid(), first.WorkItemId, work.ETag, Durable.Request(f));
        var processor = new DurableWorkflowProcessor(Durable.Worker, new Durable.Provider());
        await processor.ProcessNextAsync("worker", default); await processor.ProcessNextAsync("worker", default);
        var v1 = await f.Decisions.GetArtifactAsync(f.Actor, f.Project, first.CaseId, 1);
        await f.Decisions.DecideAsync(f.Actor, f.Project, first.CaseId, new DecisionCommand(1, 1, v1.ContentHash, ReviewDecision.Reject, "not this", Guid.NewGuid()));
        Assert.Equal(StepOutcome.Failed, await processor.ProcessNextAsync("worker", default));
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal((RunStatus.Failed, "publication_stale"), (view.Status, view.FailureCode));
        var current = await f.Store.GetWorkItemAsync(f.Actor, f.Project, first.WorkItemId);
        Assert.Equal((ReviewState.Rejected, 1), (current.ReviewState!.Value, current.ArtifactVersion!.Value));
    }

    [OrchestrationFact]
    public async Task AWorkItemWithoutACaseGetsItsFirstCaseThroughRegeneration()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        // A work item whose first M6 attempt never published (no case yet).
        var admission = await f.Store.AdmitAsync(f.Actor, f.Command, f.Token);
        await f.Store.FinishAsync(f.Actor, f.Project, admission.Outcome.RunId, f.Token, GenerationStatus.Cancelled, "generation_cancelled");
        var work = await f.Store.GetWorkItemAsync(f.Actor, f.Project, admission.Outcome.WorkItemId);
        Assert.Null(work.CaseId);
        var run = await Durable.Api.SubmitRegenerationAsync(f.Actor, Guid.NewGuid(), work.WorkItemId, work.ETag, Durable.Request(f));
        var view = await Complete(f, run.RunId);
        Assert.Equal((RunStatus.Succeeded, work.WorkItemId, 1), (view.Status, view.Publication!.WorkItemId, view.Publication.Version));
        Assert.Equal(view.Publication.CaseId, (await f.Store.GetWorkItemAsync(f.Actor, f.Project, work.WorkItemId)).CaseId);
    }

    [OrchestrationFact]
    public async Task HttpRegenerationRequiresAStrongCurrentPrecondition()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); var first = await Published(f);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenAI:ApiKey", "sk-test-placeholder-not-a-real-key-1234567890");
            TestSecurity.Configure(builder, legacyGeneration: false);
            builder.ConfigureServices(services => { services.RemoveAll<IWorkflowRunStore>(); services.AddSingleton<IWorkflowRunStore>(Durable.Api); });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestSecurity.Token(subject: f.Actor.Subject));
        var work = await f.Store.GetWorkItemAsync(f.Actor, f.Project, first.WorkItemId);
        HttpRequestMessage Request(string? tag)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{f.Project}/work-items/{first.WorkItemId:D}/runs");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
            request.Content = new StringContent(JsonSerializer.Serialize(new { task = ReviewSamples.Request.Task, context = ReviewSamples.Request.Context,
                confirmedRequirements = ReviewSamples.Request.ConfirmedRequirements, workflowVersion = LifecycleLimits.WorkflowVersion },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
            return request;
        }
        foreach (var (tag, status) in new (string?, HttpStatusCode)[] { (null, HttpStatusCode.PreconditionRequired), ("*", HttpStatusCode.BadRequest),
            ("W/\"weak\"", HttpStatusCode.BadRequest), (WorkItemETag.Format(first.WorkItemId, 99, first.CaseId, 9), HttpStatusCode.PreconditionFailed),
            (work.ETag, HttpStatusCode.Accepted) })
        {
            using var request = Request(tag); using var response = await client.SendAsync(request);
            Assert.Equal(status, response.StatusCode);
        }
        await Durable.Drain();
    }
}
