using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Core.Development;

namespace Factory.Infrastructure.GitHub;

// RemoteUrlFormat and ApiBase are replaceable for tests (a local bare repository and a fake API).
public sealed record GitHubOptions(string Repository, string Token, string RemoteUrlFormat = "https://github.com/{0}.git",
    string ApiBase = "https://api.github.com/", string WebBase = "https://github.com/", string GitPath = "git");

// M11.3: publishes a verified development job as a pull request with a repository-scoped token.
// The commits are re-created on the host from trusted data only (the regenerated base files and the
// evidence diff, applied by git) in a temporary repository isolated from the user's Git configuration,
// and must reproduce the evidence's commit ids before anything is pushed. Pushes are never forced; main
// is written only while it does not exist (the base commit); nothing is ever merged.
public sealed partial class GitHubPullRequestPublisher(GitHubOptions options, HttpClient http) : IPullRequestPublisher
{
    private static readonly string[] Identity =
    [
        "GIT_AUTHOR_NAME=Universal AI Factory", "GIT_AUTHOR_EMAIL=factory@localhost", "GIT_COMMITTER_NAME=Universal AI Factory",
        "GIT_COMMITTER_EMAIL=factory@localhost", "GIT_AUTHOR_DATE=2026-01-01T00:00:00Z", "GIT_COMMITTER_DATE=2026-01-01T00:00:00Z",
    ];

    public string Repository => options.Repository;

    public async Task<PullRequestResult> PublishAsync(PullRequestSource source, string branch, string title, string body, CancellationToken ct = default)
    {
        var evidence = source.Evidence;
        if (!PullRequestText.IsRepository(options.Repository) || !BranchName().IsMatch(branch) || evidence.Status != DevelopmentJobStatus.Verified ||
            evidence.BaseCommit is null || evidence.FinalCommit is null || string.IsNullOrEmpty(evidence.Diff))
            throw new PullRequestException("invalid_pull_request");
        var root = Directory.CreateTempSubdirectory("factory-pr-");
        try
        {
            var repo = Directory.CreateDirectory(Path.Combine(root.FullName, "repo")).FullName;
            var globalConfig = Path.Combine(root.FullName, "gitconfig");
            await File.WriteAllTextAsync(globalConfig, "", ct);
            async Task<string> Git(string failure, params string[] args)
            {
                var (exit, output) = await Run(repo, globalConfig, args, ct);
                return exit == 0 ? output : throw new PullRequestException(failure);
            }

            await Git("git_init_failed", "init", "-q", "-b", "main");
            foreach (var file in source.BaseFiles)
            {
                var path = Path.Combine(repo, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(file.Content), ct);
            }
            await Git("git_commit_failed", "add", "-A");
            await Git("git_commit_failed", "commit", "-q", "--allow-empty", "-m", "Generated starter application");
            var baseCommit = (await Git("git_commit_failed", "rev-parse", "HEAD")).Trim();
            if (baseCommit != evidence.BaseCommit) throw new PullRequestException("base_commit_mismatch");
            var patch = Path.Combine(root.FullName, "change.diff");
            await File.WriteAllBytesAsync(patch, Encoding.UTF8.GetBytes(evidence.Diff), ct);
            await Git("diff_apply_failed", "apply", "--index", "--whitespace=nowarn", patch);
            await Git("git_commit_failed", "commit", "-q", "--allow-empty", "-m", "Developer Agent changes");
            var head = (await Git("git_commit_failed", "rev-parse", "HEAD")).Trim();
            if (head != evidence.FinalCommit) throw new PullRequestException("final_commit_mismatch");

            var remote = string.Format(System.Globalization.CultureInfo.InvariantCulture, options.RemoteUrlFormat, options.Repository);
            if (string.IsNullOrWhiteSpace(await Git("remote_unavailable", "ls-remote", remote, "refs/heads/main")))
                await Git("push_failed", "push", "-q", remote, $"{baseCommit}:refs/heads/main");
            await Git("branch_conflict", "push", "-q", remote, $"{head}:refs/heads/{branch}");
            return await OpenPullRequest(branch, title, body, head, ct);
        }
        finally
        {
            try { DeleteTree(root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Idempotent: an existing pull request for the branch is returned instead of a new one.
    private async Task<PullRequestResult> OpenPullRequest(string branch, string title, string body, string head, CancellationToken ct)
    {
        var owner = options.Repository[..options.Repository.IndexOf('/')];
        using (var list = Request(HttpMethod.Get, $"repos/{options.Repository}/pulls?state=all&head={Uri.EscapeDataString(owner + ":" + branch)}"))
        using (var listed = await http.SendAsync(list, ct))
        {
            if (!listed.IsSuccessStatusCode) throw new PullRequestException("pull_request_api_failed");
            var existing = (await listed.Content.ReadFromJsonAsync<List<PullJson>>(ct))?.FirstOrDefault();
            if (existing is not null) return Validate(existing, head);
        }
        using var create = Request(HttpMethod.Post, $"repos/{options.Repository}/pulls");
        create.Content = JsonContent.Create(new { title, head = branch, @base = "main", body, maintainer_can_modify = false });
        using var created = await http.SendAsync(create, ct);
        if (!created.IsSuccessStatusCode) throw new PullRequestException("pull_request_api_failed");
        return Validate(await created.Content.ReadFromJsonAsync<PullJson>(ct) ?? throw new PullRequestException("pull_request_api_failed"), head);
    }

    private PullRequestResult Validate(PullJson pull, string head)
    {
        var prefix = options.WebBase + options.Repository + "/pull/";
        if (pull.Number <= 0 || pull.HtmlUrl is null || !pull.HtmlUrl.StartsWith(prefix, StringComparison.Ordinal) || pull.Head?.Sha != head)
            throw new PullRequestException("pull_request_unexpected");
        return new(pull.Number, pull.HtmlUrl, head);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(options.ApiBase), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("universal-ai-factory", "1.0"));
        return request;
    }

    // The token travels as an HTTP header set through environment configuration, never on the
    // command line; system and user Git configuration (credential helpers, hooks, autocrlf) is ignored.
    private async Task<(int Exit, string Output)> Run(string directory, string globalConfig, string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(options.GitPath) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-c", "core.autocrlf=false", "-c", "core.hooksPath=" + Path.Combine(directory, ".no-hooks"), "-c", "core.fileMode=false",
                     "-c", "commit.gpgSign=false", "-c", "credential.helper=" }.Concat(args))
            start.ArgumentList.Add(arg);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = globalConfig;
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_CONFIG_COUNT"] = "1";
        start.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
        start.Environment["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("x-access-token:" + options.Token));
        foreach (var pair in Identity) { var i = pair.IndexOf('='); start.Environment[pair[..i]] = pair[(i + 1)..]; }
        using var process = Process.Start(start) ?? throw new PullRequestException("git_unavailable");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await stderr;
        return (process.ExitCode, await stdout);
    }

    private static void DeleteTree(DirectoryInfo root)
    {
        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
        root.Delete(recursive: true);
    }

    private sealed record PullJson(int Number, [property: System.Text.Json.Serialization.JsonPropertyName("html_url")] string? HtmlUrl, HeadJson? Head);
    private sealed record HeadJson(string? Sha);

    [GeneratedRegex(@"\Afactory/[a-z0-9-]{1,180}\z")]
    private static partial Regex BranchName();
}
