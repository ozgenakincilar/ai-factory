using System.ClientModel;
using Factory.Core.Agents;
using Factory.Core.Workflows;
using Factory.Orchestration.Workflows;

namespace Factory.Api;

internal static class WorkflowEndpoint
{
    internal static async Task<IResult> ExecuteAsync(WorkflowRequest request,
        AnalysisArchitectureWorkflow workflow, HttpContext context, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AnalysisArchitectureWorkflow");
        WorkflowResult result;
        int statusCode;
        try
        {
            result = await workflow.ExecuteAsync(request, cancellationToken);
            statusCode = result.Status switch
            {
                WorkflowStatus.AwaitingHumanReview => 200,
                WorkflowStatus.Cancelled => 499,
                _ when result.FailureKind == AgentFailureKind.InvalidRequest => 400,
                _ when result.FailureKind == AgentFailureKind.Refused ||
                    result.Error == "no_requirements_for_architecture" => 422,
                _ => 502
            };
        }
        catch (WorkflowExecutionException ex)
        {
            result = ex.Result;
            statusCode = ex.InnerException is ClientResultException ? 502 : 500;
            logger.LogWarning("Workflow {WorkflowId}, request {TraceId}: {FailureCode}; exception type {ExceptionType}",
                result.WorkflowId, context.TraceIdentifier,
                statusCode == 502 ? "provider_failure" : "unexpected_workflow_failure", ex.InnerException?.GetType().FullName);
            if (ex.InnerException is ClientResultException sdkError)
            {
                logger.LogWarning("Workflow {WorkflowId} provider failure: HTTP {ProviderStatus}, request {RequestId}",
                    result.WorkflowId, sdkError.Status, SafeApiDiagnostics.ProviderRequestId(sdkError));
            }
            // Never log exception objects, caller fields, or provider payloads.
        }

        context.Response.Headers["X-Workflow-Id"] = result.WorkflowId.ToString();
        logger.LogInformation("Workflow {WorkflowId}: {Status}; Analyst {AnalystStatus}; Architect {ArchitectStatus}; " +
            "failed stage {FailedStage}; failure {FailureKind}; code {Error}; HTTP {StatusCode}",
            result.WorkflowId, result.Status, result.AnalystStatus, result.ArchitectStatus,
            result.FailedStage, result.FailureKind, result.Error, statusCode);
        return Results.Json(result, statusCode: statusCode);
    }
}
