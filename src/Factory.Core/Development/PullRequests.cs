using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Factory.Core.Development;

// M11.3: a verified development job becomes a GitHub pull request. The Factory pushes exactly the
// commits recorded in the evidence (re-created and compared before any push), never to main after
// the base commit, and never merges.
public sealed record PullRequestSource(Guid JobId, DevelopmentEvidence Evidence, IReadOnlyList<Templates.TemplateFile> BaseFiles);

public sealed record PullRequestResult(int Number, string Url, string HeadCommit);

public sealed record PullRequestOutcome(string Repository, string Branch, string Status, int? Number, string? Url, string? HeadCommit,
    string? FailureCode, DateTimeOffset CreatedAt);

public sealed class PullRequestException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public interface IPullRequestPublisher
{
    string Repository { get; }
    Task<PullRequestResult> PublishAsync(PullRequestSource source, string branch, string title, string body, CancellationToken ct = default);
}

public sealed record PendingPullRequest(Guid JobId, byte[] Evidence, string EvidenceSha256);

public interface IPullRequestQueue
{
    Task<PendingPullRequest?> NextPullRequestAsync(CancellationToken ct = default);
    Task RecordPullRequestAsync(Guid jobId, string repository, string branch, PullRequestResult? opened, string? failureCode, CancellationToken ct = default);
}

public static partial class PullRequestText
{
    public static bool IsRepository(string? repository) => repository is not null && Repository().IsMatch(repository);

    // factory/<application-kebab>-<first 12 hex of the job id>; unique per job, stable across retries.
    public static string Branch(DevelopmentEvidence evidence) =>
        $"factory/{Templates.ApplicationName.Create(evidence.ApplicationName).Kebab}-{evidence.JobId.ToString("N")[..12]}";

    public static string Title(DevelopmentEvidence evidence)
    {
        var requirements = evidence.Changes.SelectMany(c => c.RequirementIds).Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal).ToList();
        return $"[Factory] {evidence.ApplicationName}: implement {string.Join(", ", requirements)}";
    }

    // Markdown body: what the reviewer needs to judge the change; all values come from the evidence.
    public static string Body(DevelopmentEvidence evidence, string evidenceSha256)
    {
        var b = new StringBuilder();
        b.AppendLine("Opened by the Universal AI Factory. **Human review required: the Factory never merges.**").AppendLine();
        b.AppendLine("## Source").AppendLine();
        b.AppendLine($"- Approved review case `{evidence.CaseId}` version {evidence.ArtifactVersion}, artifact SHA-256 `{evidence.ArtifactSha256}`");
        b.AppendLine($"- Template `{evidence.TemplateId}` {evidence.TemplateVersion} (SHA-256 `{evidence.TemplateSha256}`)");
        b.AppendLine($"- Developer Agent `{evidence.Model}`: {evidence.Turns} turns, {evidence.Actions} actions, {evidence.Calls.Count} model calls, " +
            $"estimated USD {(evidence.CostMicros / 1_000_000m).ToString("0.0000", CultureInfo.InvariantCulture)}" +
            (evidence.Corrections > 0 ? $", {evidence.Corrections} correction(s) after failed checks" : ""));
        b.AppendLine($"- Commits `{evidence.BaseCommit}` → `{evidence.FinalCommit}`, diff SHA-256 `{evidence.DiffSha256}`");
        b.AppendLine($"- Development job `{evidence.JobId}`, evidence SHA-256 `{evidenceSha256}`").AppendLine();
        b.AppendLine("## Requirement traceability").AppendLine();
        b.AppendLine("| File | Change | Requirements |").AppendLine("| --- | --- | --- |");
        foreach (var change in evidence.Changes)
            b.AppendLine($"| `{Cell(change.Path)}` | {change.Kind.ToString().ToLowerInvariant()} | {string.Join(", ", change.RequirementIds.Select(Cell))} |");
        if (evidence.Checks is { Count: > 0 } checks)
        {
            b.AppendLine().AppendLine("## Factory checks").AppendLine();
            b.AppendLine("Established by the Factory (sandbox exit codes, test report, source scan), not claimed by the agent.").AppendLine();
            b.AppendLine("| Check | Result | Detail |").AppendLine("| --- | --- | --- |");
            foreach (var check in checks)
                b.AppendLine($"| {check.Name}{(check.Blocking ? "" : " (advisory)")} | {check.Status.ToString().ToLowerInvariant()} | {Cell(check.Detail)} |");
        }
        if (evidence.Verification is { } report)
        {
            b.AppendLine().AppendLine("## Offline verification").AppendLine();
            b.AppendLine($"Runner image `{report.RunnerImage}`; no network, read-only root, non-root user.").AppendLine();
            foreach (var step in report.Steps)
                b.AppendLine($"- {step.Name}: {step.Outcome.ToString().ToLowerInvariant()} (exit {step.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})");
        }
        b.AppendLine().AppendLine("Passing tests are evidence that the change builds and its tests pass, not that it is correct.");
        return b.ToString();
    }

    // Model-derived text (paths, ids, details) cannot break the table or inject markup.
    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,39}/[A-Za-z0-9_.-]{1,100}\z")]
    private static partial Regex Repository();
}
