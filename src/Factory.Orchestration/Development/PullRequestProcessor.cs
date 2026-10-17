using System.Text.Json;
using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Core.Templates;

namespace Factory.Orchestration.Development;

public enum PullRequestStep { Idle, Opened, Failed, Deferred }

// Turns the oldest verified development job without a pull request into one. Permanent problems
// (commits that do not reproduce the evidence, a diff that does not apply, a conflicting branch) are
// recorded as Failed; transient ones (network, GitHub API) are retried a few times, then recorded.
public sealed class PullRequestProcessor(IPullRequestQueue queue, ITemplateCatalog catalog, IPullRequestPublisher publisher, int maxTransientAttempts = 3)
{
    private static readonly HashSet<string> Transient = ["remote_unavailable", "push_failed", "pull_request_api_failed", "git_unavailable"];
    private readonly Dictionary<Guid, int> _attempts = [];

    // The last failure code (a fixed Factory code, safe to log).
    public string? LastFailureCode { get; private set; }

    public async Task<PullRequestStep> ProcessNextAsync(CancellationToken ct)
    {
        var pending = await queue.NextPullRequestAsync(ct);
        if (pending is null) return PullRequestStep.Idle;
        DevelopmentEvidence? evidence;
        try { evidence = JsonSerializer.Deserialize<DevelopmentEvidence>(pending.Evidence, DevelopmentEvidenceFormat.Options); }
        catch (JsonException) { evidence = null; }
        if (evidence is null || evidence.JobId != pending.JobId)
        {
            await queue.RecordPullRequestAsync(pending.JobId, publisher.Repository, "factory/invalid-evidence", null, "invalid_evidence", ct);
            return PullRequestStep.Failed;
        }
        var branch = PullRequestText.Branch(evidence);
        try
        {
            var (template, files) = catalog.Load(evidence.TemplateId, evidence.TemplateVersion);
            var application = StarterGenerator.Generate(template, files, ApplicationName.Create(evidence.ApplicationName));
            var result = await publisher.PublishAsync(new PullRequestSource(pending.JobId, evidence, application.Files), branch,
                PullRequestText.Title(evidence), PullRequestText.Body(evidence, pending.EvidenceSha256), ct);
            await queue.RecordPullRequestAsync(pending.JobId, publisher.Repository, branch, result, null, ct);
            _attempts.Remove(pending.JobId);
            return PullRequestStep.Opened;
        }
        catch (TemplateException)
        {
            await queue.RecordPullRequestAsync(pending.JobId, publisher.Repository, branch, null, "template_unavailable", ct);
            return PullRequestStep.Failed;
        }
        catch (PullRequestException ex) when (Transient.Contains(ex.Code) && Attempt(pending.JobId) < maxTransientAttempts)
        {
            LastFailureCode = ex.Code;
            return PullRequestStep.Deferred;
        }
        catch (PullRequestException ex)
        {
            LastFailureCode = ex.Code;
            await queue.RecordPullRequestAsync(pending.JobId, publisher.Repository, branch, null, ex.Code, ct);
            _attempts.Remove(pending.JobId);
            return PullRequestStep.Failed;
        }
    }

    private int Attempt(Guid job) => _attempts[job] = _attempts.GetValueOrDefault(job) + 1;
}
