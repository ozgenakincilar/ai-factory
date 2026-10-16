using System.Security.Cryptography;
using System.Text.Json;
using Factory.Core.Authorization;
using Factory.Core.Development;
using Factory.Core.Orchestration;
using NpgsqlTypes;

namespace Factory.Infrastructure.Orchestration;

// M10.3 development jobs: same roles and error mapping as verification jobs.
public sealed partial class PostgresOrchestrationStore : IDevelopmentJobs, IDevelopmentQueue
{
    public Task<DevelopmentSubmission> SubmitDevelopmentAsync(TrustedIdentity actor, string projectId, Guid key, DevelopmentRequest request,
        CancellationToken ct = default)
    {
        try { request.Validate(); }
        catch (Exception ex) when (ex is ArgumentException or Factory.Core.Templates.TemplateException)
        { throw new OrchestrationException(OrchestrationError.InvalidCommand); }
        return Read<DevelopmentSubmission>("submit_development", actor, Project(projectId), ct, P("key", key), P("case", request.CaseId),
            P("version", request.ArtifactVersion), P("hash", request.ArtifactSha256), P("template", request.TemplateId),
            P("templateversion", request.TemplateVersion), P("name", request.ApplicationName));
    }

    public Task<IReadOnlyList<DevelopmentSummary>> ListDevelopmentsAsync(TrustedIdentity actor, string projectId, CancellationToken ct = default)
        => Read<IReadOnlyList<DevelopmentSummary>>("list_developments", actor, Project(projectId), ct);

    public async Task<DevelopmentView> GetDevelopmentAsync(TrustedIdentity actor, string projectId, Guid jobId, CancellationToken ct = default)
    {
        var raw = await Read<RawDevelopment>("read_development", actor, Project(projectId), ct, P("job", jobId));
        if (raw.EvidenceSha256 is null) return new(raw.Job, null, null);
        var bytes = await GetDevelopmentEvidenceAsync(actor, projectId, jobId, ct);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != raw.EvidenceSha256) throw new OrchestrationException(OrchestrationError.Integrity);
        try
        {
            var evidence = JsonSerializer.Deserialize<DevelopmentEvidence>(bytes, DevelopmentEvidenceFormat.Options);
            if (evidence is null || evidence.JobId != jobId || evidence.ProjectId != projectId) throw new OrchestrationException(OrchestrationError.Integrity);
            return new(raw.Job, evidence, raw.EvidenceSha256);
        }
        catch (JsonException) { throw new OrchestrationException(OrchestrationError.Integrity); }
    }

    public async Task<byte[]> GetDevelopmentEvidenceAsync(TrustedIdentity actor, string projectId, Guid jobId, CancellationToken ct = default)
    {
        var encoded = await Call("SELECT pg_catalog.encode(orchestration.read_development_evidence(@scope,@issuer,@subject,@kind,@job),'base64')", ct,
            P("scope", Project(projectId)), P("issuer", actor.Issuer), P("subject", actor.Subject), P("kind", actor.Kind.ToString()), P("job", jobId));
        try { return Convert.FromBase64String(encoded ?? throw new OrchestrationException(OrchestrationError.NotFound)); }
        catch (FormatException) { throw new OrchestrationException(OrchestrationError.Integrity); }
    }

    // Jobs refused at claim (approval changed, total cap reached) are finished by the database; the
    // next queued job is tried, a bounded number of times per call.
    public async Task<ClaimedDevelopment?> ClaimDevelopmentAsync(string workerId, byte[] leaseToken, long jobCapMicros, long totalCapMicros,
        CancellationToken ct = default)
    {
        for (var i = 0; i < 10; i++)
        {
            var json = await Call("SELECT orchestration.claim_development(@worker,@token,@jobcap,@totalcap)::text", ct, P("worker", workerId),
                P("token", leaseToken), P("jobcap", jobCapMicros, NpgsqlDbType.Bigint), P("totalcap", totalCapMicros, NpgsqlDbType.Bigint));
            if (json is null) return null;
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("refused", out _)) continue;
                return document.RootElement.Deserialize<ClaimedDevelopment>(Json) ?? throw new OrchestrationException(OrchestrationError.Integrity);
            }
            catch (JsonException) { throw new OrchestrationException(OrchestrationError.Integrity); }
        }
        return null;
    }

    public Task RenewDevelopmentAsync(Guid jobId, byte[] leaseToken, CancellationToken ct = default)
        => Call("SELECT orchestration.renew_development(@job,@token)::text", ct, P("job", jobId), P("token", leaseToken));

    public Task CompleteDevelopmentAsync(Guid jobId, byte[] leaseToken, DevelopmentEvidence evidence, CancellationToken ct = default)
    {
        var content = DevelopmentEvidenceFormat.Serialize(evidence);
        if (evidence.JobId != jobId || content.Length > DevelopmentJobLimits.MaxEvidenceBytes) throw new OrchestrationException(OrchestrationError.InvalidCommand);
        return Call("SELECT orchestration.complete_development(@job,@token,@content)::text", ct, P("job", jobId), P("token", leaseToken),
            P("content", content, NpgsqlDbType.Bytea));
    }

    public Task FailDevelopmentAsync(Guid jobId, byte[] leaseToken, string failureCode, CancellationToken ct = default)
        => Call("SELECT orchestration.fail_development(@job,@token,@failure)::text", ct, P("job", jobId), P("token", leaseToken), P("failure", failureCode));

    private sealed record RawDevelopment(DevelopmentSummary Job, string? EvidenceSha256);
}
