using Factory.Core.Agents;
using Factory.Core.Analysis;
using Factory.Core.Workflows;
using Factory.Orchestration.Agents;

namespace Factory.Orchestration.Workflows;

public sealed class AnalysisArchitectureWorkflow(AnalystAgent analyst, ArchitectAgent architect)
{
    public async Task<WorkflowResult> ExecuteAsync(WorkflowRequest? request,
        CancellationToken cancellationToken = default)
    {
        var result = new WorkflowResult(Guid.NewGuid(), WorkflowStatus.Failed);
        var stage = WorkflowStage.Input;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Strings and evidence records are immutable; detach the only mutable collection
            // before validation or the first await. Never read the caller's request again.
            var snapshot = request is null ? null : new AgentRequest(request.ProjectId,
                request.Task, request.Context, request.ConfirmedRequirements?.ToArray());
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.ProjectId) ||
                string.IsNullOrWhiteSpace(snapshot.Task) || snapshot.Context is null ||
                !AnalysisValidator.HasValidSources(snapshot.ConfirmedRequirements))
                return Fail(AgentFailureKind.InvalidRequest, "invalid_workflow_request");

            stage = WorkflowStage.Analyst;
            var analysis = await analyst.ExecuteAsync(snapshot, cancellationToken);
            if (!analysis.Success) return Fail(analysis.FailureKind, analysis.Error);
            result = result with { Analysis = analysis.Analysis, AnalystStatus = WorkflowStageStatus.Completed };
            stage = WorkflowStage.Architect;
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Analysis!.FunctionalRequirements.Count == 0)
                return result with { FailedStage = stage, Error = "no_requirements_for_architecture" };

            var proposal = await architect.ExecuteAsync(snapshot with { Analysis = result.Analysis }, cancellationToken);
            if (!proposal.Success) return Fail(proposal.FailureKind, proposal.Error);
            cancellationToken.ThrowIfCancellationRequested();
            return result with { Status = WorkflowStatus.AwaitingHumanReview,
                Architecture = proposal.Architecture, ArchitectStatus = WorkflowStageStatus.Completed };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(null, "workflow_cancelled") with { Status = WorkflowStatus.Cancelled,
                AnalystStatus = stage == WorkflowStage.Analyst ? WorkflowStageStatus.Cancelled : result.AnalystStatus,
                ArchitectStatus = stage == WorkflowStage.Architect ? WorkflowStageStatus.Cancelled : result.ArchitectStatus };
        }
        catch (Exception ex)
        {
            throw new WorkflowExecutionException(Fail(null, "workflow_execution_failed"), ex);
        }

        WorkflowResult Fail(AgentFailureKind? kind, string? error) => result with
        {
            FailedStage = stage, FailureKind = kind, Error = error,
            AnalystStatus = stage == WorkflowStage.Analyst ? WorkflowStageStatus.Failed : result.AnalystStatus,
            ArchitectStatus = stage == WorkflowStage.Architect ? WorkflowStageStatus.Failed : result.ArchitectStatus
        };
    }
}
