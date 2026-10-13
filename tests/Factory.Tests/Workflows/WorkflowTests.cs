using System.ClientModel;
using System.Text.Json;
using Factory.Core.Agents;
using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Models;
using Factory.Core.Workflows;
using Factory.Orchestration.Agents;
using Factory.Orchestration.Workflows;
using Factory.Tests.Architecture;

namespace Factory.Tests.Workflows;

internal sealed class WorkflowProvider : IModelProvider
{
    internal List<ModelRequest> Requests { get; } = [];
    internal Func<int, ModelRequest, CancellationToken, Task<ModelResponse>>? Handler { get; init; }
    public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Handler?.Invoke(Requests.Count, request, cancellationToken) ?? Task.FromResult(Valid(Requests.Count));
    }
    internal static ModelResponse Valid(int call) => new(call == 1
        ? JsonSerializer.Serialize(ArchitectSamples.Request.Analysis, AnalysisJson.Options)
        : ArchitectSamples.Json, "synthetic");
}

public class WorkflowTests
{
    [Fact]
    public void CapturedLiveWorkflow_IsTypedValidatedAndUnapproved()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var request = JsonSerializer.Deserialize<WorkflowRequest>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Evidence", "workflow-live-request.json")), options)!;
        var result = JsonSerializer.Deserialize<WorkflowResult>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Evidence", "workflow-live-response.json")), options)!;
        Assert.True(result.Success);
        Assert.Equal(WorkflowStatus.AwaitingHumanReview, result.Status);
        Assert.Equal(WorkflowStageStatus.Completed, result.AnalystStatus);
        Assert.Equal(WorkflowStageStatus.Completed, result.ArchitectStatus);
        Assert.True(AnalysisValidator.IsValid(result.Analysis, request.ConfirmedRequirements));
        Assert.All(result.Analysis!.FunctionalRequirements, r =>
        {
            Assert.Equal(RequirementStatus.Suggested, r.Status);
            Assert.Null(r.ConfirmedSourceId);
        });
        Assert.True(ArchitectureValidator.IsValid(result.Architecture,
            new AgentRequest(request.ProjectId, request.Task, request.Context, request.ConfirmedRequirements, result.Analysis)));
        Assert.Equal(ProposalStatus.Proposed, result.Architecture!.Status);
        Assert.Empty(result.Architecture.ConfirmedConstraintRequirementIds);
        var failure = JsonSerializer.Deserialize<WorkflowResult>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Evidence", "workflow-live-failed-response.json")), options)!;
        Assert.Equal(WorkflowStatus.Failed, failure.Status);
        Assert.NotEqual(failure.WorkflowId, result.WorkflowId);
        Assert.Equal(WorkflowStage.Analyst, failure.FailedStage);
        Assert.Null(failure.Analysis);
        Assert.Null(failure.Architecture);
    }

    [Fact]
    public async Task ConcurrentExecutions_DoNotMixProjectContextOrIds()
    {
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var provider = new ConcurrentProvider(async (request, token) =>
        {
            using var json = JsonDocument.Parse(request.UserPrompt);
            var project = json.RootElement.GetProperty("projectId").GetString();
            if (request.OutputSchema == AnalysisJson.OutputSchema)
            {
                if (Interlocked.Increment(ref count) == 2) bothEntered.SetResult();
                await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                return new ModelResponse(JsonSerializer.Serialize(ArchitectSamples.Request.Analysis! with { Summary = project! }, AnalysisJson.Options), "synthetic");
            }
            Assert.Equal(project, json.RootElement.GetProperty("analysis").GetProperty("summary").GetString());
            return WorkflowProvider.Valid(2);
        });
        var workflow = Create(provider);
        var results = await Task.WhenAll(workflow.ExecuteAsync(Request with { ProjectId = "one" }),
            workflow.ExecuteAsync(Request with { ProjectId = "two" }));
        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(new[] { "one", "two" }, results.Select(r => r.Analysis!.Summary));
        Assert.NotEqual(results[0].WorkflowId, results[1].WorkflowId);
    }

    private sealed class ConcurrentProvider(Func<ModelRequest, CancellationToken, Task<ModelResponse>> handler) : IModelProvider
    {
        public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default) => handler(request, cancellationToken);
    }

    [Fact]
    public async Task CancellationBetweenStages_SkipsArchitectAndRetainsAnalysis()
    {
        using var cts = new CancellationTokenSource();
        var provider = new WorkflowProvider { Handler = (call, _, _) =>
        {
            cts.Cancel();
            return Task.FromResult(WorkflowProvider.Valid(call));
        }};
        var result = await Create(provider).ExecuteAsync(Request, cts.Token);
        Assert.Equal(WorkflowStatus.Cancelled, result.Status);
        Assert.Equal(WorkflowStageStatus.Completed, result.AnalystStatus);
        Assert.NotNull(result.Analysis);
        Assert.Single(provider.Requests);
    }

    internal static WorkflowRequest Request => new("sample", "Propose a minimal architecture", "No chosen stack",
        ArchitectSamples.Request.ConfirmedRequirements);
    internal static AnalysisArchitectureWorkflow Create(IModelProvider provider) => new(new AnalystAgent(provider), new ArchitectAgent(provider));

    [Fact]
    public async Task Success_PreservesTypedHandoffAndRequiresReview()
    {
        var provider = new WorkflowProvider();
        var result = await Create(provider).ExecuteAsync(Request);
        Assert.True(result.Success);
        Assert.NotEqual(Guid.Empty, result.WorkflowId);
        Assert.Equal(WorkflowStatus.AwaitingHumanReview, result.Status);
        Assert.Equal(WorkflowStageStatus.Completed, result.AnalystStatus);
        Assert.Equal(WorkflowStageStatus.Completed, result.ArchitectStatus);
        Assert.Equal(ProposalStatus.Proposed, result.Architecture!.Status);
        Assert.Equal(new[] { AnalysisJson.OutputSchema, ArchitectureJson.OutputSchema }, provider.Requests.Select(r => r.OutputSchema));
        var handoff = JsonSerializer.Deserialize<AgentRequest>(provider.Requests[1].UserPrompt, ArchitectureJson.Options)!;
        Assert.Equal(JsonSerializer.Serialize(result.Analysis, AnalysisJson.Options), JsonSerializer.Serialize(handoff.Analysis, AnalysisJson.Options));
        Assert.Equal(Request.ConfirmedRequirements, handoff.ConfirmedRequirements);
        Assert.Equal(Request.ProjectId, handoff.ProjectId);
        Assert.Equal(Request.Task, handoff.Task);
        Assert.Equal(Request.Context, handoff.Context);
        Assert.Equal(RequirementStatus.Suggested, result.Analysis!.FunctionalRequirements[1].Status);
        Assert.True(ArchitectureValidator.IsValid(result.Architecture, handoff));
    }

    [Fact]
    public async Task EvidenceSnapshot_SurvivesMutationWhileAnalystRuns()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = Request.ConfirmedRequirements!.ToList();
        var original = Request with { ConfirmedRequirements = sources };
        var provider = new WorkflowProvider { Handler = async (call, _, _) =>
        {
            if (call == 1) { entered.SetResult(); await release.Task; }
            return WorkflowProvider.Valid(call);
        }};
        var running = Create(provider).ExecuteAsync(original);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sources.Clear();
        original = original with { ProjectId = "changed", Context = "changed", Task = "changed" };
        release.SetResult();
        var result = await running;
        Assert.True(result.Success);
        var handoff = JsonSerializer.Deserialize<AgentRequest>(provider.Requests[1].UserPrompt, ArchitectureJson.Options)!;
        Assert.Equal(Request.ProjectId, handoff.ProjectId);
        Assert.Equal(Request.Context, handoff.Context);
        Assert.Equal(Request.Task, handoff.Task);
        Assert.Single(handoff.ConfirmedRequirements!);
    }

    [Theory]
    [InlineData(1, "refused")]
    [InlineData(2, "refused")]
    [InlineData(1, "incomplete")]
    [InlineData(2, "incomplete")]
    [InlineData(1, "empty")]
    [InlineData(2, "empty")]
    [InlineData(1, "malformed")]
    [InlineData(2, "malformed")]
    [InlineData(1, "invalid")]
    [InlineData(2, "invalid")]
    public async Task StageFailure_StopsAndRetainsOnlyValidatedAnalysis(int failedCall, string kind)
    {
        var provider = new WorkflowProvider { Handler = (call, _, _) => Task.FromResult(call != failedCall ? WorkflowProvider.Valid(call) : kind switch
        {
            "refused" => new ModelResponse("private", "synthetic", ModelResponseStatus.Refused),
            "incomplete" => new ModelResponse("private", "synthetic", ModelResponseStatus.Incomplete),
            "empty" => new ModelResponse("", "synthetic", ModelResponseStatus.Empty),
            "invalid" => new ModelResponse(call == 1 ? WorkflowProvider.Valid(1).Content.Replace("Add tasks", "invented")
                : ArchitectSamples.Json.Replace("FR-2", "unknown"), "synthetic"),
            _ => new ModelResponse("private", "synthetic")
        })};
        var result = await Create(provider).ExecuteAsync(Request);
        Assert.False(result.Success);
        Assert.Equal(WorkflowStatus.Failed, result.Status);
        Assert.Equal(failedCall, provider.Requests.Count);
        Assert.Equal(failedCall == 1 ? WorkflowStage.Analyst : WorkflowStage.Architect, result.FailedStage);
        Assert.Equal(failedCall == 2, result.Analysis is not null);
        Assert.Null(result.Architecture);
        Assert.DoesNotContain("private", result.Error!);
    }

    [Fact]
    public async Task EmptyRequirements_SkipsArchitect()
    {
        var provider = new WorkflowProvider { Handler = (_, _, _) => Task.FromResult(new ModelResponse(
            JsonSerializer.Serialize(ArchitectSamples.Request.Analysis! with { FunctionalRequirements = [] }, AnalysisJson.Options), "synthetic")) };
        var result = await Create(provider).ExecuteAsync(Request);
        Assert.Equal("no_requirements_for_architecture", result.Error);
        Assert.NotNull(result.Analysis);
        Assert.Equal(WorkflowStageStatus.NotStarted, result.ArchitectStatus);
        Assert.Single(provider.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Cancellation_PreservesProgressAndStops(int cancelAt)
    {
        using var cts = new CancellationTokenSource();
        var provider = new WorkflowProvider { Handler = (call, _, token) =>
        {
            Assert.Equal(cts.Token, token);
            if (call == cancelAt) { cts.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.FromResult(WorkflowProvider.Valid(call));
        }};
        if (cancelAt == 0) cts.Cancel();
        var result = await Create(provider).ExecuteAsync(Request, cts.Token);
        Assert.Equal(WorkflowStatus.Cancelled, result.Status);
        Assert.False(result.Success);
        Assert.Equal(cancelAt, provider.Requests.Count);
        Assert.Equal(cancelAt == 2, result.Analysis is not null);
        Assert.Null(result.Architecture);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Exceptions_CarryIdAndSafePartialProgress(int failedCall)
    {
        var provider = new WorkflowProvider { Handler = (call, _, _) => call == failedCall
            ? throw new ClientResultException("private upstream detail") : Task.FromResult(WorkflowProvider.Valid(call)) };
        var exception = await Assert.ThrowsAsync<WorkflowExecutionException>(() => Create(provider).ExecuteAsync(Request));
        Assert.NotEqual(Guid.Empty, exception.Result.WorkflowId);
        Assert.Equal(failedCall == 2, exception.Result.Analysis is not null);
        Assert.Equal("workflow_execution_failed", exception.Result.Error);
        Assert.Equal(failedCall, provider.Requests.Count);
    }

    [Fact]
    public async Task InvalidRequests_DoNotCallProviderAndGetUniqueIds()
    {
        var provider = new WorkflowProvider();
        var workflow = Create(provider);
        var results = new[] { await workflow.ExecuteAsync(null), await workflow.ExecuteAsync(Request with { ProjectId = "" }),
            await workflow.ExecuteAsync(Request with { ConfirmedRequirements = [new("x", "a"), new("x", "b")] }) };
        Assert.Empty(provider.Requests);
        Assert.Equal(3, results.Select(r => r.WorkflowId).Distinct().Count());
        Assert.All(results, r => Assert.Equal(AgentFailureKind.InvalidRequest, r.FailureKind));
    }
}
