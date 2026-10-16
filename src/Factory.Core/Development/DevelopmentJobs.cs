using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Core.Authorization;
using Factory.Core.Execution;

namespace Factory.Core.Development;

// M10.3: durable development jobs. A Generator asks the Developer Agent to implement the CURRENT
// APPROVED version of a review case, named by version and SHA-256; the worker re-checks the approval
// before any model call. Paid work is never re-executed automatically: a lost worker ends the job.
public enum DevelopmentJobStatus { Queued, Running, Verified, VerificationFailed, NotFinished, BudgetExhausted, ModelFailed, SandboxFailed, Failed }

public sealed record DevelopmentRequest(Guid CaseId, int ArtifactVersion, string ArtifactSha256, string TemplateId, string TemplateVersion,
    string ApplicationName)
{
    public DevelopmentRequest Validate()
    {
        if (CaseId == Guid.Empty || ArtifactVersion < 1 || ArtifactSha256 is not { Length: 64 } ||
            ArtifactSha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("invalid_development_request");
        new Execution.VerificationRequest(TemplateId, TemplateVersion, ApplicationName).Validate();
        return this;
    }
}

public sealed record DevelopmentSubmission(Guid JobId, DevelopmentJobStatus Status, bool Replayed);

public sealed record DevelopmentSummary(Guid JobId, Guid CaseId, int ArtifactVersion, string ApplicationName, DevelopmentJobStatus Status,
    string? FailureCode, long? CostMicros, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record DevelopmentView(DevelopmentSummary Job, DevelopmentEvidence? Evidence, string? EvidenceSha256);

// The approved artifact travels to the worker as its exact bytes with their hash.
public sealed record ClaimedDevelopment(Guid JobId, string ProjectId, Guid CaseId, int ArtifactVersion, string ArtifactSha256,
    string TemplateId, string TemplateVersion, string ApplicationName, string Artifact);

public sealed record EvidenceCall(int Turn, string Status, string? Model, int? InputTokens, int? OutputTokens, long ReservedMicros, long CostMicros);

// Evidence format 1: everything a reviewer needs to judge the change, in one immutable envelope.
public sealed record DevelopmentEvidence(int FormatVersion, Guid JobId, string ProjectId, Guid CaseId, int ArtifactVersion, string ArtifactSha256,
    string TemplateId, string TemplateVersion, string TemplateSha256, string ApplicationName, string Model, DevelopmentJobStatus Status,
    string? FailureCode, int Turns, int Actions, long CostMicros, IReadOnlyList<EvidenceCall> Calls, IReadOnlyList<FileChange> Changes,
    string? BaseCommit, string? FinalCommit, string? Diff, string? DiffSha256, SandboxReport? Verification, IReadOnlyList<string> Log);

public static class DevelopmentJobLimits
{
    public const int ActiveJobsPerProject = 2;
    public const int LeaseSeconds = 90;
    public const int RenewSeconds = 30;
    public const int MaxEvidenceBytes = 6 * 1024 * 1024;
}

public static class DevelopmentEvidenceFormat
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static byte[] Serialize(DevelopmentEvidence evidence) => JsonSerializer.SerializeToUtf8Bytes(evidence, Options);

    public static DevelopmentJobStatus Status(DevelopmentStatus status) => status switch
    {
        DevelopmentStatus.Verified => DevelopmentJobStatus.Verified,
        DevelopmentStatus.VerificationFailed => DevelopmentJobStatus.VerificationFailed,
        DevelopmentStatus.NotFinished => DevelopmentJobStatus.NotFinished,
        DevelopmentStatus.BudgetExhausted => DevelopmentJobStatus.BudgetExhausted,
        DevelopmentStatus.ModelFailed => DevelopmentJobStatus.ModelFailed,
        _ => DevelopmentJobStatus.SandboxFailed,
    };
}

public interface IDevelopmentJobs
{
    Task<DevelopmentSubmission> SubmitDevelopmentAsync(TrustedIdentity actor, string projectId, Guid key, DevelopmentRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<DevelopmentSummary>> ListDevelopmentsAsync(TrustedIdentity actor, string projectId, CancellationToken ct = default);
    Task<DevelopmentView> GetDevelopmentAsync(TrustedIdentity actor, string projectId, Guid jobId, CancellationToken ct = default);
    Task<byte[]> GetDevelopmentEvidenceAsync(TrustedIdentity actor, string projectId, Guid jobId, CancellationToken ct = default);
}

public interface IDevelopmentQueue
{
    // Claims the oldest queued job only while total recorded spend plus every running job's cap plus
    // this job's cap stays within totalCapMicros; otherwise the job ends as BudgetExhausted unclaimed.
    // A job whose case is no longer approved at that version and hash ends as Failed (approval_changed).
    Task<ClaimedDevelopment?> ClaimDevelopmentAsync(string workerId, byte[] leaseToken, long jobCapMicros, long totalCapMicros, CancellationToken ct = default);
    Task RenewDevelopmentAsync(Guid jobId, byte[] leaseToken, CancellationToken ct = default);
    Task CompleteDevelopmentAsync(Guid jobId, byte[] leaseToken, DevelopmentEvidence evidence, CancellationToken ct = default);
    Task FailDevelopmentAsync(Guid jobId, byte[] leaseToken, string failureCode, CancellationToken ct = default);
}
