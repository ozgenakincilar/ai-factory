using System.Text.Json;
using Factory.Core.Authorization;
using Factory.Core.Execution;
using Factory.Core.Orchestration;
using NpgsqlTypes;

namespace Factory.Infrastructure.Orchestration;

// M9.3 verification jobs: same roles as workflow runs (the API's lifecycle runtime submits and
// reads; the worker role claims, renews and completes) and the same error mapping.
public sealed partial class PostgresOrchestrationStore : IVerificationJobs, IVerificationQueue
{
    private static string Project(string projectId) => string.IsNullOrWhiteSpace(projectId) || projectId.Length > 200
        ? throw new OrchestrationException(OrchestrationError.Forbidden) : projectId;

    public Task<VerificationSubmission> SubmitVerificationAsync(TrustedIdentity actor, string projectId, Guid key, VerificationRequest request,
        CancellationToken ct = default)
    {
        try { request.Validate(); }
        catch (ArgumentException) { throw new OrchestrationException(OrchestrationError.InvalidCommand); }
        catch (Factory.Core.Templates.TemplateException) { throw new OrchestrationException(OrchestrationError.InvalidCommand); }
        return Read<VerificationSubmission>("submit_verification", actor, Project(projectId), ct, P("key", key),
            P("template", request.TemplateId), P("version", request.TemplateVersion), P("name", request.ApplicationName));
    }

    public Task<IReadOnlyList<VerificationSummary>> ListVerificationsAsync(TrustedIdentity actor, string projectId, CancellationToken ct = default)
        => Read<IReadOnlyList<VerificationSummary>>("list_verifications", actor, Project(projectId), ct);

    public async Task<VerificationView> GetVerificationAsync(TrustedIdentity actor, string projectId, Guid jobId, CancellationToken ct = default)
    {
        var raw = await Read<RawVerification>("read_verification", actor, Project(projectId), ct, P("job", jobId));
        if (raw.EvidenceSha256 is null) return new(raw.Job, null, null);
        var bytes = await GetVerificationEvidenceAsync(actor, projectId, jobId, ct);
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != raw.EvidenceSha256)
            throw new OrchestrationException(OrchestrationError.Integrity);
        try
        {
            var evidence = JsonSerializer.Deserialize<VerificationEvidence>(bytes, VerificationEvidenceFormat.Options);
            if (evidence is null || evidence.JobId != jobId || evidence.ProjectId != projectId) throw new OrchestrationException(OrchestrationError.Integrity);
            return new(raw.Job, evidence, raw.EvidenceSha256);
        }
        catch (JsonException) { throw new OrchestrationException(OrchestrationError.Integrity); }
    }

    // The exact stored evidence bytes (their SHA-256 is the evidence hash).
    public async Task<byte[]> GetVerificationEvidenceAsync(TrustedIdentity actor, string projectId, Guid jobId, CancellationToken ct = default)
    {
        var encoded = await Call("SELECT pg_catalog.encode(orchestration.read_verification_evidence(@scope,@issuer,@subject,@kind,@job),'base64')", ct,
            P("scope", Project(projectId)), P("issuer", actor.Issuer), P("subject", actor.Subject), P("kind", actor.Kind.ToString()), P("job", jobId));
        try { return Convert.FromBase64String(encoded ?? throw new OrchestrationException(OrchestrationError.NotFound)); }
        catch (FormatException) { throw new OrchestrationException(OrchestrationError.Integrity); }
    }

    public async Task<ClaimedVerification?> ClaimVerificationAsync(string workerId, byte[] leaseToken, CancellationToken ct = default)
    {
        var json = await Call("SELECT orchestration.claim_verification(@worker,@token)::text", ct, P("worker", workerId), P("token", leaseToken));
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<ClaimedVerification>(json, Json) ?? throw new OrchestrationException(OrchestrationError.Integrity); }
        catch (JsonException) { throw new OrchestrationException(OrchestrationError.Integrity); }
    }

    public Task RenewVerificationAsync(Guid jobId, byte[] leaseToken, CancellationToken ct = default)
        => Call("SELECT orchestration.renew_verification(@job,@token)::text", ct, P("job", jobId), P("token", leaseToken));

    public Task CompleteVerificationAsync(Guid jobId, byte[] leaseToken, VerificationEvidence evidence, CancellationToken ct = default)
    {
        var content = VerificationEvidenceFormat.Serialize(evidence);
        if (evidence.JobId != jobId || content.Length > VerificationLimits.MaxEvidenceBytes) throw new OrchestrationException(OrchestrationError.InvalidCommand);
        return Call("SELECT orchestration.complete_verification(@job,@token,@content)::text", ct, P("job", jobId), P("token", leaseToken),
            P("content", content, NpgsqlDbType.Bytea));
    }

    public Task FailVerificationAsync(Guid jobId, byte[] leaseToken, string failureCode, CancellationToken ct = default)
        => Call("SELECT orchestration.fail_verification(@job,@token,@failure)::text", ct, P("job", jobId), P("token", leaseToken), P("failure", failureCode));

    private sealed record RawVerification(VerificationSummary Job, string? EvidenceSha256);
}
