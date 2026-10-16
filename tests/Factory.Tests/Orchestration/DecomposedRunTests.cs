using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core.Decomposition;
using Factory.Core.Orchestration;
using Factory.Infrastructure.Reviews;
using Factory.Orchestration.Durable;
using Factory.Tests.Integration;
using Factory.Tests.Lifecycle;
using Factory.Tests.Reviews;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Factory.Tests.Orchestration;

[Collection("Lifecycle database")]
public class DecomposedRunTests
{
    private static Task<RunSubmission> Submit(LifecycleFixture f)
        => Durable.Api.SubmitAsync(f.Actor, Guid.NewGuid(), Durable.Request(f), WorkflowVersions.Decomposition);

    private static DurableWorkflowProcessor Processor(DecompositionSamples.Provider provider)
        => new(Durable.Worker, new RegistryModelResolver(Durable.Worker, new ModelCatalog(new CatalogEntry(provider, null)), null));

    // Runs Analysis and Planning, then parks plan approval; returns the pending decision.
    private static async Task<StepDecision> PlanAndWait(LifecycleFixture f, Guid run, DurableWorkflowProcessor processor)
    {
        for (var i = 0; i < 2; i++) Assert.Equal(StepOutcome.Completed, await processor.ProcessNextAsync("worker", default));
        Assert.Equal(StepOutcome.Suspended, await processor.ProcessNextAsync("worker", default));
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run);
        Assert.Equal(RunStatus.Waiting, view.Status);
        // Nothing beyond the fixed steps exists before approval.
        Assert.Equal(5, view.Steps.Count);
        return Assert.Single(view.Decisions!);
    }

    [OrchestrationFact]
    public async Task AnApprovedPlanRunsIndependentPartsInParallelAndPublishesTheMergedProposal()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var run = (await Submit(f)).RunId;
        var provider = new DecompositionSamples.Provider { AwaitParallelParts = 2 };
        var processor = Processor(provider);
        var decision = await PlanAndWait(f, run, processor);
        Assert.Equal(("plan_approval", DecisionStatus.Pending), (decision.Reason, decision.Status));
        Assert.Equal(["approve", "stop"], decision.Options);
        var plan = await Durable.Api.GetPlanAsync(f.Actor, f.Project, run);
        Assert.Equal(DecompositionSamples.PlanJson, JsonSerializer.Serialize(plan.Plan, DecompositionJson.Options));
        Assert.Equal([new PartRoute("P1", ModelTier.Economy, "role_minimum"), new PartRoute("P2", ModelTier.Standard, "complexity"),
            new PartRoute("P3", ModelTier.Strong, "high_importance")], plan.Routes);
        Assert.Equal(0, provider.Parts);

        await Durable.Api.DecideAsync(f.Actor, f.Project, run, decision.Id, "approve");
        Assert.Equal(StepOutcome.Completed, await processor.ProcessNextAsync("worker", default)); // approval creates the parts
        // P1 and P2 are independent: two workers hold them at the same time.
        var first = processor.ProcessNextAsync("worker-a", default);
        for (var i = 0; i < 250 && Volatile.Read(ref provider.Parts) < 1; i++) await Task.Delay(20);
        var second = processor.ProcessNextAsync("worker-b", default);
        Assert.Equal([StepOutcome.Completed, StepOutcome.Completed], await Task.WhenAll(first, second));
        Assert.Equal(2, provider.PeakParallelParts);
        // P3 (needs P1 and P2), the merge and publication follow in order.
        for (var i = 0; i < 3; i++) Assert.Equal(StepOutcome.Completed, await processor.ProcessNextAsync("worker", default));
        Assert.Equal(StepOutcome.Idle, await processor.ProcessNextAsync("worker", default));

        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run);
        Assert.Equal(RunStatus.Succeeded, view.Status);
        Assert.Equal(8, view.Steps.Count);
        Assert.Equal([StepKind.Part, StepKind.Part, StepKind.Part], view.Steps.Skip(5).Select(s => s.Kind));
        Assert.Equal((1, 1, 3), (provider.Analysis, provider.Planning, provider.Parts));
        Assert.Equal([["FR-1"], ["FR-2"], ["FR-1", "FR-2"]], provider.PartRequirementIds.OrderBy(ids => ids.Length).ThenBy(ids => ids[0]).ToArray());
        // Routes: Analysis (Standard), Planning (Strong) and each part by its classification; approval,
        // merge and publication make no model call.
        Assert.Equal([(0, ModelTier.Standard), (3, ModelTier.Strong), (5, ModelTier.Economy), (6, ModelTier.Standard), (7, ModelTier.Strong)],
            view.Routes!.Select(r => (r.Ordinal, r.Tier)));
        // The published artifact carries the merged proposal (part-prefixed ids) under the unchanged format.
        var artifact = await new PostgresReviewStore(LifecycleFixture.Options(LifecycleFixture.Owner))
            .GetArtifactAsync(f.Project, view.Publication!.CaseId, view.Publication.Version);
        var envelope = ReviewArtifactSerializer.Read(artifact!);
        Assert.Equal(["P1.C-FR-1", "P2.C-FR-2", "P3.C-FR-1", "P3.C-FR-2"], envelope.Architecture.Components.Select(c => c.Id));
    }

    [OrchestrationFact]
    public async Task StoppingThePlanCancelsTheRunBeforeAnyPartRuns()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var run = (await Submit(f)).RunId; var provider = new DecompositionSamples.Provider(); var processor = Processor(provider);
        var decision = await PlanAndWait(f, run, processor);
        var view = await Durable.Api.DecideAsync(f.Actor, f.Project, run, decision.Id, "stop");
        Assert.Equal(RunStatus.Cancelled, view.Status);
        Assert.Equal(StepOutcome.Idle, await processor.ProcessNextAsync("worker", default));
        Assert.Equal(0, provider.Parts);
    }

    [OrchestrationFact]
    public async Task AnInvalidPlanFailsTheRunWithoutAskingTheCustomer()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var run = (await Submit(f)).RunId;
        var provider = new DecompositionSamples.Provider
        {
            PlanContent = JsonSerializer.Serialize(DecompositionSamples.Plan with { Parts = [DecompositionSamples.Plan.Parts[1]] }, DecompositionJson.Options)
        };
        var processor = Processor(provider);
        Assert.Equal(StepOutcome.Completed, await processor.ProcessNextAsync("worker", default));
        Assert.Equal(StepOutcome.Failed, await processor.ProcessNextAsync("worker", default));
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run);
        Assert.Equal((RunStatus.Failed, "invalid_output", 0), (view.Status, view.FailureCode, provider.Parts));
        Assert.Null(view.Decisions);
    }

    [OrchestrationFact]
    public async Task AFailedPartFailsTheRunAndEndsTheLeaseOfARunningSibling()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var run = (await Submit(f)).RunId; var provider = new DecompositionSamples.Provider(); var processor = Processor(provider);
        var decision = await PlanAndWait(f, run, processor);
        await Durable.Api.DecideAsync(f.Actor, f.Project, run, decision.Id, "approve");
        await processor.ProcessNextAsync("worker", default);
        // A second worker holds P1 while P2 fails.
        var token = RandomNumberGenerator.GetBytes(32); var sibling = (await Durable.Worker.ClaimAsync("sibling", token))!;
        Assert.Equal((5, StepKind.Part, "P1"), (sibling.Ordinal, sibling.Kind, sibling.Part!.Key));
        provider.FailParts = true;
        Assert.Equal(StepOutcome.Failed, await processor.ProcessNextAsync("worker", default));
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run);
        Assert.Equal((RunStatus.Failed, "generation_failed"), (view.Status, view.FailureCode));
        Assert.Equal([StepStatus.Cancelled, StepStatus.Failed, StepStatus.Cancelled], view.Steps.Skip(5).Select(s => s.Status));
        var input = new Factory.Core.Agents.AgentRequest(f.Project, "task", "context", ReviewSamples.Request.ConfirmedRequirements, DecompositionSamples.Analysis);
        var proposal = DecompositionSamples.ProposalFor(PartScope.Request(input, sibling.Part, []).Analysis!);
        Assert.Equal(OrchestrationError.LeaseLost, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Worker.CompletePartAsync(sibling.AttemptId, token, proposal))).Error);
    }

    [OrchestrationFact]
    public async Task PlansAreReadableOnlyByTheProjectAndTheDatabaseRevalidatesThem()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var run = (await Submit(f)).RunId;
        async Task<OrchestrationError> Error(Func<Task> action) => (await Assert.ThrowsAsync<OrchestrationException>(action)).Error;
        Assert.Equal(OrchestrationError.NotFound, await Error(() => Durable.Api.GetPlanAsync(f.Actor, f.Project, run)));
        var processor = Processor(new DecompositionSamples.Provider());
        Assert.Equal(StepOutcome.Completed, await processor.ProcessNextAsync("worker", default)); // analysis
        // The database refuses a plan that drops a requirement, even from a worker holding the lease.
        var token = RandomNumberGenerator.GetBytes(32); var planning = (await Durable.Worker.ClaimAsync("worker", token))!;
        Assert.Equal(StepKind.Planning, planning.Kind);
        var uncovered = DecompositionSamples.Plan with { Parts = [DecompositionSamples.Plan.Parts[0]] };
        Assert.Equal(OrchestrationError.InvalidCommand, await Error(() => Durable.Worker.CompletePlanningAsync(planning.AttemptId, token, uncovered)));
        await Durable.Worker.CompletePlanningAsync(planning.AttemptId, token, DecompositionSamples.Plan);
        Assert.Equal(OrchestrationError.Forbidden, await Error(() => Durable.Api.GetPlanAsync(other.Actor, f.Project, run)));
        Assert.Equal(OrchestrationError.NotFound, await Error(() => Durable.Api.GetPlanAsync(f.Actor, f.Project, Guid.NewGuid())));
        Assert.Equal(3, (await Durable.Api.GetPlanAsync(f.Actor, f.Project, run)).Plan.Parts.Count);
        // Approval cannot be completed without a recorded "approve" decision.
        var approvalToken = RandomNumberGenerator.GetBytes(32); var approval = (await Durable.Worker.ClaimAsync("worker", approvalToken))!;
        Assert.Equal(StepKind.PlanApproval, approval.Kind);
        Assert.Equal(OrchestrationError.InvalidCommand, await Error(() => Durable.Worker.ApprovePlanAsync(approval.AttemptId, approvalToken)));
        foreach (var connection in new[] { LifecycleFixture.Runtime, Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")! })
        {
            await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
            await using var command = new NpgsqlCommand("""SELECT * FROM orchestration."StepDependencies" """, db);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task HttpSelectsTheDecomposedWorkflowAndServesThePlan()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenAI:ApiKey", "sk-test-placeholder-not-a-real-key-1234567890");
            TestSecurity.Configure(builder);
            builder.ConfigureServices(services => { services.RemoveAll<IWorkflowRunStore>(); services.AddSingleton<IWorkflowRunStore>(Durable.Api); });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestSecurity.Token(subject: f.Actor.Subject));
        HttpRequestMessage Submit(string version)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{f.Project}/runs");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                task = ReviewSamples.Request.Task, context = ReviewSamples.Request.Context,
                confirmedRequirements = ReviewSamples.Request.ConfirmedRequirements, workflowVersion = version
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
            return request;
        }
        using (var unknown = await client.SendAsync(Submit("decomposition-v2"))) Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var accepted = await client.SendAsync(Submit(WorkflowVersions.Decomposition));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var run = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetGuid();
        using (var early = await client.GetAsync($"/api/projects/{f.Project}/runs/{run:D}/plan")) Assert.Equal(HttpStatusCode.NotFound, early.StatusCode);
        var processor = Processor(new DecompositionSamples.Provider());
        for (var i = 0; i < 2; i++) await processor.ProcessNextAsync("worker", default);
        using var plan = await client.GetAsync($"/api/projects/{f.Project}/runs/{run:D}/plan");
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        Assert.Equal("no-store", plan.Headers.CacheControl!.ToString());
        var body = JsonDocument.Parse(await plan.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("high", body.GetProperty("plan").GetProperty("parts")[2].GetProperty("importance").GetString());
        Assert.Equal("strong", body.GetProperty("routes")[2].GetProperty("tier").GetString());
        // The run view stays a safe projection: no part titles or scopes.
        using var status = await client.GetAsync($"/api/projects/{f.Project}/runs/{run:D}");
        Assert.DoesNotContain("Task entry", await status.Content.ReadAsStringAsync());
        await Durable.Drain();
    }
}
