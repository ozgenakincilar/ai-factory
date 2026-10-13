using Factory.Core.Agents;
using Factory.Core.Analysis;
using Factory.Core.Architecture;

namespace Factory.Core.Workflows;

public sealed record WorkflowRequest(string ProjectId, string Task, string Context,
    IReadOnlyList<ConfirmedRequirement>? ConfirmedRequirements = null);

// Generation succeeds into review; this workflow has no approval capability.
public enum WorkflowStatus { AwaitingHumanReview, Failed, Cancelled }
public enum WorkflowStageStatus { NotStarted, Completed, Failed, Cancelled }
public enum WorkflowStage { Input, Analyst, Architect }

public sealed record WorkflowResult(
    Guid WorkflowId,
    WorkflowStatus Status,
    WorkflowStageStatus AnalystStatus = WorkflowStageStatus.NotStarted,
    WorkflowStageStatus ArchitectStatus = WorkflowStageStatus.NotStarted,
    AnalysisResult? Analysis = null,
    ArchitectureProposal? Architecture = null,
    WorkflowStage? FailedStage = null,
    AgentFailureKind? FailureKind = null,
    string? Error = null)
{
    public bool Success => Status == WorkflowStatus.AwaitingHumanReview;
}

// Carries safe partial progress while allowing the API to classify SDK exceptions.
public sealed class WorkflowExecutionException(WorkflowResult result, Exception innerException)
    : Exception("Workflow execution failed.", innerException)
{
    public WorkflowResult Result { get; } = result;
}
