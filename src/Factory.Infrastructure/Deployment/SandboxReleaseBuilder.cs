using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Factory.Core.Deployment;
using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Core.Templates;
using Factory.Infrastructure.Execution;

namespace Factory.Infrastructure.Deployment;

// M13A: builds the release package of a verified development job in the offline sandbox. The
// evidence's commits are re-created from trusted data (the regenerated base files and the evidence
// diff) and both ids must match before anything is built, so the package comes from exactly the
// verified source. The publish output is untrusted and validated entry by entry.
public sealed class SandboxReleaseBuilder(DockerSandboxOptions options) : IReleaseBuilder
{
    private const string Output = SandboxStep.Root + "/.factory-release";
    private const string Patch = "factory-release.diff";

    public async Task<ReleasePackage> BuildAsync(Guid developmentJobId, DevelopmentEvidence evidence, IReadOnlyList<TemplateFile> baseFiles,
        CancellationToken ct = default)
    {
        if (evidence.Status != DevelopmentJobStatus.Verified || evidence.BaseCommit is not { Length: 40 } || evidence.FinalCommit is not { Length: 40 } ||
            string.IsNullOrEmpty(evidence.Diff) || evidence.JobId != developmentJobId)
            throw new DeploymentException(DeploymentError.InvalidRelease);
        var name = ApplicationName.Create(evidence.ApplicationName);
        await using var session = await DockerWorkspaceSession.StartAsync(options, baseFiles, ct);
        async Task<string> Run(string step, DeploymentError failure, params string[] arguments)
        {
            var result = await session.RunAsync(new SandboxStep(step, arguments), ct);
            if (result.Outcome != StepOutcome.Passed) throw new DeploymentException(failure, step + "_failed: " + Tail(result.Output));
            return result.Output;
        }
        var git = GitSnapshot.Git;
        await Run("git-init", DeploymentError.ReleaseBuildFailed, [.. git, "init", "-q", "-b", "main"]);
        await Run("git-base", DeploymentError.ReleaseBuildFailed, [.. git, "add", "-A"]);
        await Run("git-base-commit", DeploymentError.ReleaseBuildFailed, [.. git, "commit", "-q", "--allow-empty", "-m", "Generated starter application"]);
        if ((await Run("git-rev-parse", DeploymentError.ReleaseBuildFailed, [.. git, "rev-parse", "HEAD"])).Trim() != evidence.BaseCommit)
            throw new DeploymentException(DeploymentError.ReleaseCommitMismatch, "base_commit_mismatch");
        await session.WriteAsync(Patch, evidence.Diff, ct);
        await Run("git-apply", DeploymentError.ReleaseCommitMismatch, [.. git, "apply", "--index", "--whitespace=nowarn", Patch]);
        await Run("patch-remove", DeploymentError.ReleaseBuildFailed, "rm", "-f", "--", Patch);
        await Run("git-final-commit", DeploymentError.ReleaseBuildFailed, [.. git, "commit", "-q", "--allow-empty", "-m", "Developer Agent changes"]);
        if ((await Run("git-rev-parse", DeploymentError.ReleaseBuildFailed, [.. git, "rev-parse", "HEAD"])).Trim() != evidence.FinalCommit)
            throw new DeploymentException(DeploymentError.ReleaseCommitMismatch, "final_commit_mismatch");
        // M14: the Angular build is served by the API, so it goes into the API's wwwroot before publishing.
        if (baseFiles.Any(f => f.Path == "web/package.json"))
        {
            const string web = SandboxStep.Root + "/web";
            async Task Web(string step, params string[] arguments)
            {
                var result = await session.RunAsync(new SandboxStep(step, arguments, web), ct);
                if (result.Outcome != StepOutcome.Passed) throw new DeploymentException(DeploymentError.ReleaseBuildFailed, step + "_failed: " + Tail(result.Output));
            }
            await Web("npm-ci", "npm", "ci", "--offline", "--ignore-scripts");
            await Web("web-build", "npx", "--no-install", "ng", "build");
            // Fixed timestamps: publish writes the files' modification times into the static web assets
            // manifest (Last-Modified), which would make every rebuild's package different.
            await Run("web-copy", DeploymentError.ReleaseBuildFailed, "sh", "-c",
                "mkdir -p \"$1\" && cp -R \"$2\"/. \"$1\"/ && find \"$1\" -exec touch -h -d 2026-01-01T00:00:00Z {} +", "sh",
                $"{SandboxStep.Root}/src/{name.Value}.Api/wwwroot", $"{web}/dist/{name.Kebab}/browser");
        }
        var project = $"src/{name.Value}.Api/{name.Value}.Api.csproj";
        await Run("dotnet-publish", DeploymentError.ReleaseBuildFailed,
            "dotnet", "publish", project, "-c", "Release", "-o", Output, "-nodeReuse:false", "-p:NuGetAudit=false", "-p:UseAppHost=false",
            // Build-time compressed copies of static assets carry build-time Last-Modified values in the endpoints manifest.
            "-p:CompressionEnabled=false");
        byte[] tar;
        try { tar = await session.ExportAsync(Output, ReleasePackage.MaxBytes, ct); }
        catch (SandboxUnavailableException e) when (e.Code == "output_too_large") { throw new DeploymentException(DeploymentError.PackageTooLarge); }
        var zip = ReleaseArchive.FromTar(tar);
        return new ReleasePackage(developmentJobId, name.Kebab, evidence.FinalCommit, ReleasePackage.Hash(zip), zip).Validate();
    }

    private static string Tail(string text) => text.Length <= 2000 ? text : text[^2000..];
}

public static partial class ReleaseArchive
{
    public const int MaxFiles = 2000;
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Regular files and directories only (no links, devices or absolute/parent paths); the zip is
    // deterministic: ordinal path order, fixed timestamps.
    public static byte[] FromTar(byte[] tar)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        using (var reader = new TarReader(new MemoryStream(tar)))
        {
            while (reader.GetNextEntry(copyData: true) is { } entry)
            {
                var path = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
                if (entry.EntryType == TarEntryType.Directory || path is "" or ".") continue;
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || !SafePath().IsMatch(path) ||
                    path.Split('/').Any(s => s is "" or "." or ".."))
                    throw new DeploymentException(DeploymentError.InvalidRelease, "unsafe_release_entry");
                using var data = new MemoryStream();
                entry.DataStream?.CopyTo(data);
                total += data.Length;
                if (files.Count >= MaxFiles || total > ReleasePackage.MaxBytes) throw new DeploymentException(DeploymentError.PackageTooLarge);
                if (!files.TryAdd(path, data.ToArray())) throw new DeploymentException(DeploymentError.InvalidRelease, "duplicate_release_entry");
            }
        }
        if (files.Count == 0) throw new DeploymentException(DeploymentError.InvalidRelease, "empty_release");
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = FixedTime;
                using var stream = entry.Open();
                stream.Write(content);
            }
        return buffer.ToArray();
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._\-+/]{1,260}\z")]
    private static partial Regex SafePath();
}
