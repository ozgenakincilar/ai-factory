using System.Text;
using System.Text.Json;
using Factory.Core.Execution;
using Factory.Core.Templates;

namespace Factory.Infrastructure.Templates;

// Minimal template registry: <root>/registry.json lists template versions and their content hash;
// each version's files live in <root>/<directory>. Loading verifies the hash, so a changed or
// tampered template is refused instead of generating something unreviewed.
public sealed class FileSystemTemplateCatalog(string root) : ITemplateCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private sealed record Registry(IReadOnlyList<TemplateDescriptor>? Templates);

    public IReadOnlyList<TemplateDescriptor> List()
    {
        Registry? registry;
        try { registry = JsonSerializer.Deserialize<Registry>(File.ReadAllText(Path.Combine(root, "registry.json")), Json); }
        catch (JsonException) { throw new TemplateException(TemplateError.InvalidRegistry); }
        var templates = registry?.Templates ?? throw new TemplateException(TemplateError.InvalidRegistry);
        if (templates.Any(t => string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Version) ||
                !TemplateContent.IsSafePath(t.Directory ?? "") || t.Directory!.Contains('/') ||
                string.IsNullOrEmpty(t.Token) || string.IsNullOrEmpty(t.KebabToken) || t.ContentSha256 is not { Length: 64 }) ||
            templates.Select(t => (t.Id, t.Version)).Distinct().Count() != templates.Count)
            throw new TemplateException(TemplateError.InvalidRegistry);
        return templates;
    }

    public (TemplateDescriptor Template, IReadOnlyList<TemplateFile> Files) Load(string id, string version)
    {
        var template = List().SingleOrDefault(t => t.Id == id && t.Version == version)
            ?? throw new TemplateException(TemplateError.TemplateNotFound);
        var files = TemplateContent.Normalize(Read(Path.Combine(root, template.Directory)));
        if (TemplateContent.Hash(files) != template.ContentSha256) throw new TemplateException(TemplateError.TemplateHashMismatch);
        return (template, files);
    }

    // Reads the current files of a template directory (also used to compute a new version's hash).
    public static IEnumerable<TemplateFile> Read(string directory)
    {
        var baseDirectory = new DirectoryInfo(directory);
        if (!baseDirectory.Exists) throw new TemplateException(TemplateError.TemplateNotFound);
        foreach (var file in Enumerate(baseDirectory))
        {
            var relative = Path.GetRelativePath(baseDirectory.FullName, file.FullName).Replace('\\', '/');
            string content;
            try { content = StrictUtf8.GetString(File.ReadAllBytes(file.FullName)); }
            catch (DecoderFallbackException) { throw new TemplateException(TemplateError.InvalidTemplateContent); }
            yield return new TemplateFile(relative, content);
        }
    }

    // Links are refused: a template must not pull in files from outside its directory.
    private static IEnumerable<FileInfo> Enumerate(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null) throw new TemplateException(TemplateError.InvalidTemplatePath);
            if (entry is DirectoryInfo child)
            {
                if (TemplateContent.IgnoredDirectories.Contains(child.Name)) continue;
                foreach (var file in Enumerate(child)) yield return file;
            }
            else if (entry is FileInfo file) yield return file;
        }
    }
}

public static class GeneratedApplicationWriter
{
    // Writes into a new or empty directory only; never merges into existing content.
    public static void Write(GeneratedApplication application, string target)
    {
        var directory = new DirectoryInfo(target);
        if (directory.Exists && directory.EnumerateFileSystemInfos().Any()) throw new IOException("target_directory_not_empty");
        directory.Create();
        var full = directory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in application.Files)
        {
            var path = Path.GetFullPath(Path.Combine(full, file.Path));
            if (!path.StartsWith(full, StringComparison.Ordinal)) throw new TemplateException(TemplateError.InvalidTemplatePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(file.Content));
        }
    }
}
