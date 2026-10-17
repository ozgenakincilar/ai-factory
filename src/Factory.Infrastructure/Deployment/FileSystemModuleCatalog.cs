using System.Text.Json;
using Factory.Core.Deployment;
using Factory.Core.Templates;
using Factory.Infrastructure.Templates;

namespace Factory.Infrastructure.Deployment;

// <root>/registry.json lists module versions and their content hash; each version's files live in
// <root>/<directory>. Hashing and file rules are the template ones (UTF-8 text, LF, no links).
public sealed class FileSystemModuleCatalog(string root) : IDeploymentModuleCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Registry(IReadOnlyList<DeploymentModule>? Modules);

    public IReadOnlyList<DeploymentModule> List()
    {
        Registry? registry;
        try { registry = JsonSerializer.Deserialize<Registry>(File.ReadAllText(Path.Combine(root, "registry.json")), Json); }
        catch (JsonException) { throw new DeploymentException(DeploymentError.InvalidModule); }
        var modules = registry?.Modules ?? throw new DeploymentException(DeploymentError.InvalidModule);
        if (modules.Any(m => string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Version) || !TemplateContent.IsSafePath(m.Directory ?? "") ||
                m.ContentSha256 is not { Length: 64 }) ||
            modules.Select(m => (m.Id, m.Version)).Distinct().Count() != modules.Count)
            throw new DeploymentException(DeploymentError.InvalidModule);
        return modules;
    }

    public (DeploymentModule Module, IReadOnlyList<TemplateFile> Files) Load(string id, string version)
    {
        var module = List().SingleOrDefault(m => m.Id == id && m.Version == version) ?? throw new DeploymentException(DeploymentError.InvalidModule);
        IReadOnlyList<TemplateFile> files;
        try { files = TemplateContent.Normalize(FileSystemTemplateCatalog.Read(Path.Combine(root, module.Directory))); }
        catch (TemplateException) { throw new DeploymentException(DeploymentError.InvalidModule); }
        if (TemplateContent.Hash(files) != module.ContentSha256) throw new DeploymentException(DeploymentError.ModuleHashMismatch);
        return (module, files);
    }
}
