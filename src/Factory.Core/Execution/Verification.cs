using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Core.Authorization;
using Factory.Core.Templates;

namespace Factory.Core.Execution;

// M9.3: durable verification jobs. A Generator asks the Factory to generate an application from a
// registered template and verify it in the sandbox; the worker stores the sandbox report as
// immutable, hashed evidence. Jobs have their own queue; they are not workflow runs.
public enum VerificationStatus { Queued, Running, Passed, Failed, TimedOut, SandboxError }

public sealed record VerificationRequest(string TemplateId, string TemplateVersion, string ApplicationName)
{
    public const int MaxIdLength = 100;

    public VerificationRequest Validate()
    {
        if (string.IsNullOrWhiteSpace(TemplateId) || TemplateId.Length > MaxIdLength ||
            string.IsNullOrWhiteSpace(TemplateVersion) || TemplateVersion.Length > 50)
            throw new ArgumentException("invalid_verification_request");
        Factory.Core.Templates.ApplicationName.Create(ApplicationName);
        return this;
    }
}

public sealed record VerificationSubmission(Guid JobId, VerificationStatus Status, bool Replayed);

// Safe projection: no requester identity, lease data or worker ids.
public sealed record VerificationSummary(Guid JobId, string TemplateId, string TemplateVersion, string ApplicationName,
    VerificationStatus Status, string? FailureCode, int Attempts, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

// Evidence is the exact stored bytes (format version 1); EvidenceSha256 is their SHA-256.
public sealed record VerificationView(VerificationSummary Job, VerificationEvidence? Evidence, string? EvidenceSha256);

public sealed record VerificationEvidence(int FormatVersion, Guid JobId, string ProjectId, string TemplateId, string TemplateVersion,
    string TemplateSha256, string ApplicationName, string ManifestSha256, SandboxReport Report);

public sealed record ClaimedVerification(Guid JobId, string ProjectId, string TemplateId, string TemplateVersion, string ApplicationName, int Attempt);

public static class VerificationLimits
{
    public const int ActiveJobsPerProject = 5;
    public const int MaxAttempts = 2;
    // Fixed by the database; the worker renews well within it.
    public const int LeaseSeconds = 90;
    public const int RenewSeconds = 30;
    public const int MaxEvidenceBytes = 1024 * 1024;
}

public static class VerificationEvidenceFormat
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static byte[] Serialize(VerificationEvidence evidence) => JsonSerializer.SerializeToUtf8Bytes(evidence, Options);

    // Maps the sandbox outcome to the job's terminal status.
    public static VerificationStatus Status(SandboxOutcome outcome) => outcome switch
    {
        SandboxOutcome.Passed => VerificationStatus.Passed,
        SandboxOutcome.Failed => VerificationStatus.Failed,
        SandboxOutcome.TimedOut => VerificationStatus.TimedOut,
        _ => VerificationStatus.SandboxError,
    };
}

// API-side port; same audiences as workflow runs: Generators submit, Generators and human
// Reviewers of the project read.
public interface IVerificationJobs
{
    Task<VerificationSubmission> SubmitVerificationAsync(TrustedIdentity actor, string projectId, Guid key, VerificationRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<VerificationSummary>> ListVerificationsAsync(TrustedIdentity actor, string projectId, CancellationToken ct = default);
    Task<VerificationView> GetVerificationAsync(TrustedIdentity actor, string projectId, Guid jobId, CancellationToken ct = default);
    // The exact stored evidence bytes; NotFound until the job has evidence.
    Task<byte[]> GetVerificationEvidenceAsync(TrustedIdentity actor, string projectId, Guid jobId, CancellationToken ct = default);
}

// Worker-side port. leaseToken is private worker ownership; only its digest is stored.
public interface IVerificationQueue
{
    Task<ClaimedVerification?> ClaimVerificationAsync(string workerId, byte[] leaseToken, CancellationToken ct = default);
    Task RenewVerificationAsync(Guid jobId, byte[] leaseToken, CancellationToken ct = default);
    // Stores the evidence and finishes the job atomically; the status follows the report outcome.
    Task CompleteVerificationAsync(Guid jobId, byte[] leaseToken, VerificationEvidence evidence, CancellationToken ct = default);
    // Ends the job without evidence (the template could not be loaded or generated).
    Task FailVerificationAsync(Guid jobId, byte[] leaseToken, string failureCode, CancellationToken ct = default);
}

// Port for loading a registered, hash-verified template version.
public interface ITemplateCatalog
{
    (TemplateDescriptor Template, IReadOnlyList<TemplateFile> Files) Load(string id, string version);
}
