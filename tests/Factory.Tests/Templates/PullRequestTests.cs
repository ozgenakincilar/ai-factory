using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Core.Templates;
using Factory.Infrastructure.Execution;
using Factory.Infrastructure.GitHub;
using Factory.Infrastructure.Templates;
using Factory.Orchestration.Development;

namespace Factory.Tests.Templates;

internal static class PullRequestSamples
{
    internal static DevelopmentEvidence Evidence(Guid job, string? baseCommit = null, string? finalCommit = null, string? diff = null,
        IReadOnlyList<FileChange>? changes = null) => new(1, job, "p1", Guid.NewGuid(), 1, new string('a', 64), "dotnet-angular-postgres-ddd", "1.0.0",
        new string('b', 64), "SampleShop", "gpt-5.4-mini", DevelopmentJobStatus.Verified, null, 3, 12, 64_500,
        [new EvidenceCall(1, "completed", "gpt-5.4-mini-2026-03-17", 1000, 200, 50_000, 1_650)],
        changes ?? [new FileChange("src/SampleShop.Domain/Note.cs", FileChangeKind.Added, ["FR-1"])],
        baseCommit ?? new string('1', 40), finalCommit ?? new string('2', 40), diff ?? "diff --git a/x b/x", new string('3', 64),
        DeveloperScripts.Report([new TemplateFile("a.txt", "a")], SandboxOutcome.Passed),
        ["turn 1: plan"], [new VerificationCheck("layers", CheckStatus.Passed, true, "no forbidden references between layers"),
            new VerificationCheck("new_tests", CheckStatus.Warning, false, "the change adds no tests")], 1);
}

public class PullRequestTextTests
{
    [Fact]
    public void BranchTitleAndBody_DescribeTheEvidence_ForHumanReview()
    {
        var job = Guid.Parse("0123456789abcdef0123456789abcdef");
        var evidence = PullRequestSamples.Evidence(job, changes:
        [
            new("src/SampleShop.Domain/Products/Product.cs", FileChangeKind.Added, ["USER-1", "USER-2"]),
            new("src/SampleShop.Api/Program.cs", FileChangeKind.Modified, ["USER-4"]),
        ]);
        Assert.Equal("factory/sample-shop-0123456789ab", PullRequestText.Branch(evidence));
        Assert.Equal("[Factory] SampleShop: implement USER-1, USER-2, USER-4", PullRequestText.Title(evidence));
        var body = PullRequestText.Body(evidence, new string('e', 64));
        Assert.StartsWith("Opened by the Universal AI Factory. **Human review required: the Factory never merges.**", body);
        Assert.Contains("| `src/SampleShop.Domain/Products/Product.cs` | added | USER-1, USER-2 |", body);
        Assert.Contains("| new_tests (advisory) | warning | the change adds no tests |", body);
        Assert.Contains("estimated USD 0.0645, 1 correction(s) after failed checks", body);
        Assert.Contains($"evidence SHA-256 `{new string('e', 64)}`", body);
        Assert.Contains("- test-report: passed (exit 0)", body);
    }

    [Fact]
    public void ModelDerivedText_CannotBreakTheMarkdown()
    {
        var evidence = PullRequestSamples.Evidence(Guid.NewGuid(), changes: [new("src/A.cs", FileChangeKind.Added, ["FR-1"])]) with
        {
            Checks = [new VerificationCheck("layers", CheckStatus.Failed, true, "x | y `z`\n<script>alert(1)</script>")],
        };
        var body = PullRequestText.Body(evidence, new string('e', 64));
        Assert.Contains("| layers | failed | x \\| y 'z' &lt;script>alert(1)&lt;/script> |", body);
        Assert.DoesNotContain("<script>", body);
    }

    [Theory]
    [InlineData("ozgenakincilar/stockflow", true)]
    [InlineData("owner/repo.name_1-x", true)]
    [InlineData("ozgenakincilar", false)]
    [InlineData("a/b/c", false)]
    [InlineData("owner/../x", false)]
    [InlineData("owner/repo name", false)]
    public void RepositoriesAreOwnerSlashName(string repository, bool valid) => Assert.Equal(valid, PullRequestText.IsRepository(repository));
}

public class PullRequestProcessorTests
{
    private sealed class Queue(byte[]? evidence, Guid job) : IPullRequestQueue
    {
        public bool Pending = true;
        public List<(string Branch, PullRequestResult? Opened, string? Failure)> Recorded { get; } = [];
        public Task<PendingPullRequest?> NextPullRequestAsync(CancellationToken ct = default) =>
            Task.FromResult(Pending && evidence is not null ? new PendingPullRequest(job, evidence, new string('e', 64)) : null);
        public Task RecordPullRequestAsync(Guid jobId, string repository, string branch, PullRequestResult? opened, string? failureCode, CancellationToken ct = default)
        { Recorded.Add((branch, opened, failureCode)); Pending = false; return Task.CompletedTask; }
    }

    private sealed class Publisher(params Exception?[] outcomes) : IPullRequestPublisher
    {
        private int _next;
        public string Repository => "ozgenakincilar/stockflow";
        public List<PullRequestSource> Sources { get; } = [];
        public Task<PullRequestResult> PublishAsync(PullRequestSource source, string branch, string title, string body, CancellationToken ct = default)
        {
            Sources.Add(source);
            var outcome = outcomes[Math.Min(_next++, outcomes.Length - 1)];
            return outcome is null ? Task.FromResult(new PullRequestResult(7, "https://github.com/ozgenakincilar/stockflow/pull/7", source.Evidence.FinalCommit!)) : throw outcome;
        }
    }

    private static (Queue, PullRequestProcessor, Publisher) Create(params Exception?[] outcomes)
    {
        var job = Guid.NewGuid();
        var queue = new Queue(DevelopmentEvidenceFormat.Serialize(PullRequestSamples.Evidence(job)), job);
        var publisher = new Publisher(outcomes);
        return (queue, new PullRequestProcessor(queue, new FileSystemTemplateCatalog(TemplateRepository.Root()), publisher), publisher);
    }

    [Fact]
    public async Task VerifiedJobs_ArePublishedFromTheRegeneratedBase_AndRecorded()
    {
        var (queue, processor, publisher) = Create((Exception?)null);
        Assert.Equal(PullRequestStep.Opened, await processor.ProcessNextAsync(default));
        Assert.Contains(publisher.Sources.Single().BaseFiles, f => f.Path == "SampleShop.slnx");
        Assert.Equal(7, queue.Recorded.Single().Opened!.Number);
        Assert.Equal(PullRequestStep.Idle, await processor.ProcessNextAsync(default));
    }

    [Fact]
    public async Task TransientFailures_AreRetried_ThenRecorded_AndPermanentOnesAtOnce()
    {
        var (queue, processor, _) = Create(new PullRequestException("push_failed"));
        Assert.Equal(PullRequestStep.Deferred, await processor.ProcessNextAsync(default));
        Assert.Equal(PullRequestStep.Deferred, await processor.ProcessNextAsync(default));
        Assert.Empty(queue.Recorded);
        Assert.Equal(PullRequestStep.Failed, await processor.ProcessNextAsync(default));
        Assert.Equal("push_failed", queue.Recorded.Single().Failure);

        var (permanentQueue, permanent, _) = Create(new PullRequestException("final_commit_mismatch"));
        Assert.Equal(PullRequestStep.Failed, await permanent.ProcessNextAsync(default));
        Assert.Equal("final_commit_mismatch", permanentQueue.Recorded.Single().Failure);
    }
}

// The publisher against a local bare repository and a fake GitHub API, with real evidence commits.
[Collection(DockerCollection.Name)]
public class GitHubPublisherTests
{
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];
        public string? CreatedHead;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            if (request.Method == HttpMethod.Get)
                return Json(CreatedHead is null ? "[]" : $$$"""[{"number":3,"html_url":"https://github.com/ozgenakincilar/stockflow/pull/3","head":{"sha":"{{{CreatedHead}}}"}}]""");
            using var document = JsonDocument.Parse(body!);
            CreatedHead = Rev(document.RootElement.GetProperty("head").GetString()!);
            return Json($$$"""{"number":3,"html_url":"https://github.com/ozgenakincilar/stockflow/pull/3","head":{"sha":"{{{CreatedHead}}}"}}""", HttpStatusCode.Created);
        }
        public string Remote = "";
        private string Rev(string branch) => Git(Remote, "rev-parse", "refs/heads/" + branch).Trim();
        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    internal static string Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
        return process.ExitCode == 0 ? output : "";
    }

    [SandboxFact]
    public async Task VerifiedCommits_ArePushedToAFactoryBranch_AndOpenedOnce_NeverMerged()
    {
        var options = new DockerSandboxOptions(Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE")!, SandboxLimits.Default);
        var application = DevelopmentWorkspaceTests.Application("SampleShop");
        var workspace = new DevelopmentWorkspace(application);
        workspace.Write("src/SampleShop.Domain/Note.cs", "namespace SampleShop.Domain;\n// note\n", ["FR-1"]);
        var diff = await GitSnapshot.CreateAsync(options, workspace.BaseFiles, workspace.Files);
        var job = Guid.NewGuid();
        var evidence = PullRequestSamples.Evidence(job, diff.BaseCommit, diff.FinalCommit, diff.Diff, workspace.Changes());
        var root = Directory.CreateTempSubdirectory("factory-remote-");
        try
        {
            var remote = Path.Combine(root.FullName, "stockflow.git");
            Git(root.FullName, "init", "-q", "--bare", "-b", "main", remote);
            var api = new FakeGitHub { Remote = remote };
            var publisher = new GitHubPullRequestPublisher(new GitHubOptions("ozgenakincilar/stockflow", "test-token",
                RemoteUrlFormat: remote.Replace('\\', '/'), ApiBase: "https://api.example.test/"), new HttpClient(api));
            var branch = PullRequestText.Branch(evidence);
            var result = await publisher.PublishAsync(new PullRequestSource(job, evidence, application.Files), branch,
                PullRequestText.Title(evidence), PullRequestText.Body(evidence, new string('e', 64)));
            Assert.Equal((3, "https://github.com/ozgenakincilar/stockflow/pull/3", diff.FinalCommit), (result.Number, result.Url, result.HeadCommit));
            // The host re-created exactly the evidence commits: main holds the base, the branch the change.
            Assert.Equal(diff.BaseCommit, Git(remote, "rev-parse", "refs/heads/main").Trim());
            Assert.Equal(diff.FinalCommit, Git(remote, "rev-parse", "refs/heads/" + branch).Trim());
            var post = api.Requests.Single(r => r.Method == HttpMethod.Post);
            Assert.Equal("/repos/ozgenakincilar/stockflow/pulls", post.Path);
            using (var payload = JsonDocument.Parse(post.Body!))
            {
                Assert.Equal(("main", branch, false), (payload.RootElement.GetProperty("base").GetString(), payload.RootElement.GetProperty("head").GetString(),
                    payload.RootElement.GetProperty("maintainer_can_modify").GetBoolean()));
                Assert.Contains("Human review required", payload.RootElement.GetProperty("body").GetString());
            }

            // Publishing again returns the existing pull request; no second one, and never a merge.
            Assert.Equal(3, (await publisher.PublishAsync(new PullRequestSource(job, evidence, application.Files), branch, "t", "b")).Number);
            Assert.Single(api.Requests, r => r.Method == HttpMethod.Post);
            Assert.DoesNotContain(api.Requests, r => r.Path.Contains("/merge", StringComparison.Ordinal) || r.Method == HttpMethod.Put);

            // A diff that does not reproduce the evidence commit is refused before anything is pushed.
            var tampered = evidence with { JobId = Guid.NewGuid(), Diff = diff.Diff.Replace("// note", "// changed", StringComparison.Ordinal) };
            var tamperedBranch = PullRequestText.Branch(tampered);
            Assert.Equal("final_commit_mismatch", (await Assert.ThrowsAsync<PullRequestException>(() =>
                publisher.PublishAsync(new PullRequestSource(tampered.JobId, tampered, application.Files), tamperedBranch, "t", "b"))).Code);
            Assert.Equal("", Git(remote, "rev-parse", "--verify", "-q", "refs/heads/" + tamperedBranch).Trim());
        }
        finally
        {
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            root.Delete(recursive: true);
        }
    }
}
