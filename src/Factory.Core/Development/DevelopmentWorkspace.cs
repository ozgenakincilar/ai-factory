using System.Text;
using System.Text.RegularExpressions;
using Factory.Core.Templates;

namespace Factory.Core.Development;

// M10.1: the Developer Agent's working copy. This aggregate is the authoritative state of the code:
// every agent edit goes through it, reads are answered from it, and the reviewable diff and final
// verification are produced from it, never from files that untrusted code may have changed while
// building or testing in the sandbox.
public enum FileChangeKind { Added, Modified, Deleted }

public sealed record FileChange(string Path, FileChangeKind Kind, IReadOnlyList<string> RequirementIds);

public enum WorkspaceError { InvalidPath, ProtectedPath, NotFound, TooLarge, InvalidContent, MissingRequirement }

public sealed class WorkspaceException(WorkspaceError error) : Exception("workspace_" + error)
{
    public WorkspaceError Error { get; } = error;
}

public static partial class WorkspaceRules
{
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxFiles = 600;
    public const int MaxTotalBytes = 8 * 1024 * 1024;
    public const int MaxSearchMatches = 100;
    public const int MaxRequirementIds = 20;

    // Files that hold the structure the Factory enforces: the solution and project files (layers and
    // references), build settings, the architecture tests, package manifests and Git metadata. The agent
    // implements features inside the layers; it cannot loosen the rules that check them.
    public static bool IsProtected(string path, ApplicationName name)
    {
        var file = path[(path.LastIndexOf('/') + 1)..];
        return file.StartsWith(".git", StringComparison.Ordinal) || file.EndsWith(".csproj", StringComparison.Ordinal) ||
            path is "global.json" or "Directory.Build.props" or "Directory.Build.targets" or "Directory.Packages.props" or "nuget.config" or "NuGet.Config" ||
            path == name.Value + ".slnx" || path == $"tests/{name.Value}.Tests/ArchitectureTests.cs" ||
            path is "web/package.json" or "web/package-lock.json" or "web/angular.json" or "web/.npmrc" ||
            (path.StartsWith("web/tsconfig", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal));
    }

    // Safe relative path that is not build output, package content or Git metadata.
    public static bool IsWorkspacePath(string? path) => path is not null && TemplateContent.IsSafePath(path) &&
        path.Split('/').All(s => s != ".git" && !TemplateContent.IgnoredDirectories.Contains(s));

    public static bool IsRequirementId(string? id) => id is not null && RequirementId().IsMatch(id);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex RequirementId();
}

public sealed class DevelopmentWorkspace
{
    private readonly Dictionary<string, string> _base;
    private readonly SortedDictionary<string, string> _files;
    private readonly Dictionary<string, SortedSet<string>> _requirements = new(StringComparer.Ordinal);

    public DevelopmentWorkspace(GeneratedApplication application)
    {
        Name = application.Name;
        BaseManifestSha256 = application.ManifestSha256;
        _base = application.Files.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
        _files = new SortedDictionary<string, string>(_base, StringComparer.Ordinal);
    }

    public ApplicationName Name { get; }
    public string BaseManifestSha256 { get; }

    public IReadOnlyList<TemplateFile> BaseFiles => _base.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new TemplateFile(f.Key, f.Value)).ToList();
    public IReadOnlyList<TemplateFile> Files => _files.Select(f => new TemplateFile(f.Key, f.Value)).ToList();
    public string ManifestSha256 => TemplateContent.Hash(Files);

    public string Read(string path)
    {
        RequirePath(path);
        return _files.TryGetValue(path, out var content) ? content : throw new WorkspaceException(WorkspaceError.NotFound);
    }

    // Paths under a directory prefix ("" for all), in ordinal order.
    public IReadOnlyList<string> List(string prefix = "")
    {
        if (prefix.Length > 0 && !WorkspaceRules.IsWorkspacePath(prefix.TrimEnd('/'))) throw new WorkspaceException(WorkspaceError.InvalidPath);
        var start = prefix.Length == 0 ? "" : prefix.TrimEnd('/') + "/";
        return _files.Keys.Where(p => p.StartsWith(start, StringComparison.Ordinal)).ToList();
    }

    // Literal, case-sensitive search; "path:line: text" for at most MaxSearchMatches lines.
    public IReadOnlyList<string> Search(string text, string prefix = "")
    {
        if (string.IsNullOrEmpty(text) || text.Length > 200 || text.Contains('\n')) throw new WorkspaceException(WorkspaceError.InvalidContent);
        var matches = new List<string>();
        foreach (var path in List(prefix))
        {
            var lines = _files[path].Split('\n');
            for (var i = 0; i < lines.Length && matches.Count < WorkspaceRules.MaxSearchMatches; i++)
                if (lines[i].Contains(text, StringComparison.Ordinal))
                    matches.Add($"{path}:{i + 1}: {(lines[i].Length > 200 ? lines[i][..200] : lines[i])}");
            if (matches.Count >= WorkspaceRules.MaxSearchMatches) break;
        }
        return matches;
    }

    public void Write(string path, string content, IReadOnlyList<string> requirementIds)
    {
        RequireEditable(path);
        RequireRequirements(requirementIds);
        if (content is null || content.Contains('\0')) throw new WorkspaceException(WorkspaceError.InvalidContent);
        content = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetByteCount(content);
        var existing = _files.TryGetValue(path, out var old) ? Encoding.UTF8.GetByteCount(old) : 0;
        if (bytes > WorkspaceRules.MaxFileBytes || (old is null && _files.Count >= WorkspaceRules.MaxFiles) ||
            TotalBytes() - existing + bytes > WorkspaceRules.MaxTotalBytes)
            throw new WorkspaceException(WorkspaceError.TooLarge);
        if (_files.Keys.Any(p => p != path && string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            throw new WorkspaceException(WorkspaceError.InvalidPath);
        _files[path] = content;
        Track(path, requirementIds);
    }

    public void Delete(string path, IReadOnlyList<string> requirementIds)
    {
        RequireEditable(path);
        RequireRequirements(requirementIds);
        if (!_files.Remove(path)) throw new WorkspaceException(WorkspaceError.NotFound);
        Track(path, requirementIds);
    }

    // Net changes against the generated base, each with the requirement ids its edits named.
    public IReadOnlyList<FileChange> Changes()
    {
        var changes = new List<FileChange>();
        foreach (var path in _base.Keys.Union(_files.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            var before = _base.TryGetValue(path, out var b) ? b : null;
            var after = _files.TryGetValue(path, out var a) ? a : null;
            if (before == after) continue;
            var kind = before is null ? FileChangeKind.Added : after is null ? FileChangeKind.Deleted : FileChangeKind.Modified;
            changes.Add(new FileChange(path, kind, _requirements.TryGetValue(path, out var ids) ? ids.ToList() : []));
        }
        return changes;
    }

    private long TotalBytes() => _files.Values.Sum(v => (long)Encoding.UTF8.GetByteCount(v));

    private void Track(string path, IReadOnlyList<string> ids)
    {
        if (!_requirements.TryGetValue(path, out var set)) _requirements[path] = set = new SortedSet<string>(StringComparer.Ordinal);
        set.UnionWith(ids);
    }

    private static void RequirePath(string path)
    {
        if (!WorkspaceRules.IsWorkspacePath(path)) throw new WorkspaceException(WorkspaceError.InvalidPath);
    }

    private void RequireEditable(string path)
    {
        RequirePath(path);
        if (WorkspaceRules.IsProtected(path, Name)) throw new WorkspaceException(WorkspaceError.ProtectedPath);
    }

    private static void RequireRequirements(IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count is 0 or > WorkspaceRules.MaxRequirementIds || !ids.All(WorkspaceRules.IsRequirementId))
            throw new WorkspaceException(WorkspaceError.MissingRequirement);
    }
}
