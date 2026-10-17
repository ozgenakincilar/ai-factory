using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Factory.Core.Templates;

// M9.1: versioned starter templates and deterministic generation. Pure domain logic: no file
// system, process or network access. Template content is UTF-8 text with LF line endings.
public sealed record TemplateFile(string Path, string Content);

// A registered template version. The content hash pins the exact files; Token/KebabToken are the
// placeholder application names used inside the template (it builds and tests as-is under them).
// ReadinessPath (1.1.0+) is the endpoint deployments check, which also needs the database.
public sealed record TemplateDescriptor(string Id, string Version, string Directory, string Token, string KebabToken, string ContentSha256,
    string? ReadinessPath = null);

public sealed record GeneratedApplication(string TemplateId, string TemplateVersion, string TemplateSha256,
    ApplicationName Name, IReadOnlyList<TemplateFile> Files, string ManifestSha256);

public enum TemplateError
{
    InvalidApplicationName, InvalidTemplatePath, InvalidTemplateContent, TemplateTooLarge, TemplateHashMismatch,
    TemplateNotFound, InvalidRegistry
}

public sealed class TemplateException(TemplateError error) : Exception("template_" + error)
{
    public TemplateError Error { get; } = error;
}

// PascalCase name used for namespaces, project and file names; the kebab form names the web app.
public sealed partial record ApplicationName
{
    private ApplicationName(string value, string kebab) { Value = value; Kebab = kebab; }

    public string Value { get; }
    public string Kebab { get; }

    public static ApplicationName Create(string? value)
    {
        if (value is null || !Pascal().IsMatch(value)) throw new TemplateException(TemplateError.InvalidApplicationName);
        return new(value, Boundary().Replace(value, "-").ToLowerInvariant());
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"\A[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+){0,5}\z")]
    private static partial Regex Pascal();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex Boundary();
}

public static partial class TemplateContent
{
    public const int MaxFiles = 500;
    public const int MaxBytes = 4 * 1024 * 1024;

    // Directories that are build or package output, never template content.
    public static readonly IReadOnlySet<string> IgnoredDirectories =
        new HashSet<string>(StringComparer.Ordinal) { "bin", "obj", "node_modules", "dist", ".angular", "TestResults" };

    // Relative, '/'-separated, no empty/dot segments; letters, digits, '.', '_' and '-' only.
    public static bool IsSafePath(string path) =>
        path.Length is > 0 and <= 260 && SafePath().IsMatch(path) &&
        path.Split('/').All(s => s.Length > 0 && s != "." && s != "..");

    // Text only (no NUL), LF line endings; validated before hashing or generation.
    public static IReadOnlyList<TemplateFile> Normalize(IEnumerable<TemplateFile> files)
    {
        var result = new List<TemplateFile>();
        var total = 0L;
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (!IsSafePath(file.Path)) throw new TemplateException(TemplateError.InvalidTemplatePath);
            if (file.Content.Contains('\0')) throw new TemplateException(TemplateError.InvalidTemplateContent);
            var content = file.Content.Replace("\r\n", "\n", StringComparison.Ordinal);
            total += Encoding.UTF8.GetByteCount(content);
            result.Add(file with { Content = content });
        }
        if (result.Count is 0 or > MaxFiles || total > MaxBytes) throw new TemplateException(TemplateError.TemplateTooLarge);
        if (result.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new TemplateException(TemplateError.InvalidTemplatePath);
        return result;
    }

    // SHA-256 over "path NUL content NUL" for every file in ordinal path order. Unambiguous because
    // neither paths nor content contain NUL.
    public static string Hash(IReadOnlyList<TemplateFile> normalized)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] separator = [0];
        foreach (var file in normalized)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(file.Path));
            sha.AppendData(separator);
            sha.AppendData(Encoding.UTF8.GetBytes(file.Content));
            sha.AppendData(separator);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._\-/]+\z")]
    private static partial Regex SafePath();
}

public static class StarterGenerator
{
    // Same template, version and name always give the same files and manifest hash.
    public static GeneratedApplication Generate(TemplateDescriptor template, IEnumerable<TemplateFile> source, ApplicationName name)
    {
        var files = TemplateContent.Normalize(source);
        if (TemplateContent.Hash(files) != template.ContentSha256) throw new TemplateException(TemplateError.TemplateHashMismatch);
        // A name containing a placeholder would make leftover-placeholder checks meaningless.
        if (name.Value.Contains(template.Token, StringComparison.OrdinalIgnoreCase) ||
            name.Kebab.Contains(template.KebabToken, StringComparison.Ordinal))
            throw new TemplateException(TemplateError.InvalidApplicationName);
        var generated = TemplateContent.Normalize(files.Select(f => new TemplateFile(Replace(f.Path, template, name), Replace(f.Content, template, name))));
        return new(template.Id, template.Version, template.ContentSha256, name, generated, TemplateContent.Hash(generated));
    }

    private static string Replace(string text, TemplateDescriptor template, ApplicationName name) => text
        .Replace(template.Token, name.Value, StringComparison.Ordinal)
        .Replace(template.KebabToken, name.Kebab, StringComparison.Ordinal);
}
