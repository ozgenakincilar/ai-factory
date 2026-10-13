using System.Security.Cryptography;
using System.Text.Json;
using Factory.Core.Agents;
using Factory.Core.Architecture;
using Factory.Core.Reviews;
using Factory.Core.Workflows;

namespace Factory.Infrastructure.Reviews;

public static class ReviewArtifactSerializer
{
    public const int FormatVersion = 1;
    public const string ValidatorVersion = "milestone3-v1";

    public static ReviewArtifact Create(Guid caseId, int version, WorkflowRequest request,
        WorkflowResult result, DateTimeOffset createdAt)
    {
        if (!result.Success || result.WorkflowId == Guid.Empty || result.Error is not null ||
            result.FailureKind is not null || result.FailedStage is not null ||
            result.AnalystStatus != WorkflowStageStatus.Completed || result.ArchitectStatus != WorkflowStageStatus.Completed)
            throw new ArgumentException("workflow_not_reviewable");
        try
        {
            // Serialize before validation: validators and persistence inspect the same detached bytes.
            var utc = createdAt.ToUniversalTime();
            utc = new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero); // PostgreSQL microseconds
            var envelope = new ReviewEnvelope(FormatVersion, ValidatorVersion, caseId, version,
                result.WorkflowId, utc, request, result.Analysis!, result.Architecture!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ArchitectureJson.Options);
            var artifact = new ReviewArtifact(caseId, version, bytes, Hash(bytes));
            Read(artifact);
            return artifact;
        }
        catch (JsonException) { throw new ArgumentException("invalid_review_artifact"); }
    }

    public static ReviewEnvelope Read(ReviewArtifact artifact)
    {
        var bytes = artifact.GetContent();
        if (!string.Equals(Hash(bytes), artifact.ContentHash, StringComparison.Ordinal)) throw new ReviewIntegrityException();
        try
        {
            var e = JsonSerializer.Deserialize<ReviewEnvelope>(bytes, ArchitectureJson.Options);
            if (e is null || e.FormatVersion != FormatVersion || e.ValidatorVersion != ValidatorVersion ||
                e.CaseId == Guid.Empty || e.CaseId != artifact.CaseId || e.Version < 1 || e.Version != artifact.Version ||
                e.WorkflowId == Guid.Empty || e.Request is null || e.Request.Context is null ||
                !ArchitectureValidator.IsValid(e.Architecture, new AgentRequest(e.Request.ProjectId,
                    e.Request.Task, e.Request.Context, e.Request.ConfirmedRequirements, e.Analysis)))
                throw new ReviewIntegrityException();
            return e;
        }
        catch (JsonException) { throw new ReviewIntegrityException(); }
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
