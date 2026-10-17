using System.Diagnostics;
using System.Text;
using Factory.Core.Templates;
using Factory.Infrastructure.Templates;

namespace Factory.Tests.Templates;

public static class TemplateRepository
{
    public const string Id = "dotnet-angular-postgres-ddd";
    public const string Version = "1.0.0";
    public const string Current = "1.1.3";

    public static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "templates", "registry.json"))) return Path.Combine(dir.FullName, "templates");
        throw new InvalidOperationException("templates_not_found");
    }

    public static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "factory-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public class StarterTemplateTests
{
    private static readonly (TemplateDescriptor Template, IReadOnlyList<TemplateFile> Files) Starter =
        new FileSystemTemplateCatalog(TemplateRepository.Root()).Load(TemplateRepository.Id, TemplateRepository.Version);

    private static TemplateException Fails(Action action) => Assert.Throws<TemplateException>(action);

    [Fact]
    public void RegistryPinsTheRepositoryTemplate_WithTheMandatoryDddLayers()
    {
        Assert.Equal("starter-v1", Starter.Template.Directory);
        var paths = Starter.Files.Select(f => f.Path).ToArray();
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure", "Api" })
            Assert.Contains($"src/FactoryStarter.{layer}/FactoryStarter.{layer}.csproj", paths);
        Assert.Contains("tests/FactoryStarter.Tests/ArchitectureTests.cs", paths);
        Assert.Contains("web/package-lock.json", paths);
        Assert.DoesNotContain(paths, p => p.Contains("/bin/") || p.Contains("/obj/") || p.Contains("node_modules"));
        // The domain project has no package or project references.
        Assert.DoesNotContain("Reference", Starter.Files.Single(f => f.Path == "src/FactoryStarter.Domain/FactoryStarter.Domain.csproj").Content
            .Split('\n').Where(l => !l.TrimStart().StartsWith("<!--", StringComparison.Ordinal)).Aggregate("", (a, l) => a + l));
    }

    [Fact]
    public void Generation_IsDeterministic_AndReplacesEveryPlaceholder()
    {
        var name = ApplicationName.Create("StockFlow");
        var first = StarterGenerator.Generate(Starter.Template, Starter.Files, name);
        var second = StarterGenerator.Generate(Starter.Template, Starter.Files.Reverse(), name);
        Assert.Equal(first.ManifestSha256, second.ManifestSha256);
        Assert.Equal(first.Files.Select(f => (f.Path, f.Content)), second.Files.Select(f => (f.Path, f.Content)));
        Assert.Equal(Starter.Template.ContentSha256, first.TemplateSha256);
        Assert.Equal(Starter.Files.Count, first.Files.Count);
        Assert.Contains(first.Files, f => f.Path == "src/StockFlow.Domain/StockFlow.Domain.csproj");
        Assert.Contains(first.Files, f => f.Path == "StockFlow.slnx");
        Assert.Contains("\"name\": \"stock-flow\"", first.Files.Single(f => f.Path == "web/package.json").Content);
        Assert.Contains("\"stock-flow\": {", first.Files.Single(f => f.Path == "web/angular.json").Content);
        Assert.Contains("namespace StockFlow.Domain.Common;", first.Files.Single(f => f.Path == "src/StockFlow.Domain/Common/Entity.cs").Content);
        foreach (var file in first.Files)
        {
            Assert.DoesNotContain("factorystarter", file.Path.ToLowerInvariant());
            Assert.DoesNotContain("factorystarter", file.Content.ToLowerInvariant());
            Assert.DoesNotContain("factory-starter", file.Content);
            Assert.DoesNotContain("factory_starter", file.Content);
        }
        Assert.NotEqual(first.ManifestSha256, StarterGenerator.Generate(Starter.Template, Starter.Files, ApplicationName.Create("OrderDesk")).ManifestSha256);
    }

    [Theory]
    [InlineData("StockFlow", "stock-flow")]
    [InlineData("Shop2Go", "shop2-go")]
    [InlineData("Inventory", "inventory")]
    public void ApplicationNames_DeriveTheWebName(string value, string kebab) => Assert.Equal(kebab, ApplicationName.Create(value).Kebab);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stockFlow")]
    [InlineData("API")]
    [InlineData("Stock_Flow")]
    [InlineData("Stock-Flow")]
    [InlineData("S")]
    [InlineData("../Escape")]
    [InlineData("OneTwoThreeFourFiveSixSeven")]
    [InlineData("Stöck")]
    public void InvalidApplicationNames_AreRejected(string? value) =>
        Assert.Equal(TemplateError.InvalidApplicationName, Fails(() => ApplicationName.Create(value)).Error);

    [Fact]
    public void NamesContainingThePlaceholder_AreRejected() =>
        Assert.Equal(TemplateError.InvalidApplicationName,
            Fails(() => StarterGenerator.Generate(Starter.Template, Starter.Files, ApplicationName.Create("FactoryStarterShop"))).Error);

    [Fact]
    public void ChangedTemplateContent_IsRefused_ButLineEndingsDoNotMatter()
    {
        var name = ApplicationName.Create("StockFlow");
        var tampered = Starter.Files.Select(f => f.Path == "src/FactoryStarter.Api/Program.cs" ? f with { Content = f.Content + "// extra\n" } : f);
        Assert.Equal(TemplateError.TemplateHashMismatch, Fails(() => StarterGenerator.Generate(Starter.Template, tampered, name)).Error);
        var added = Starter.Files.Append(new TemplateFile("src/Extra.cs", "class Extra {}\n"));
        Assert.Equal(TemplateError.TemplateHashMismatch, Fails(() => StarterGenerator.Generate(Starter.Template, added, name)).Error);
        var crlf = Starter.Files.Select(f => f with { Content = f.Content.Replace("\n", "\r\n", StringComparison.Ordinal) });
        Assert.Equal(StarterGenerator.Generate(Starter.Template, Starter.Files, name).ManifestSha256,
            StarterGenerator.Generate(Starter.Template, crlf, name).ManifestSha256);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("a//b.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("C:x.txt")]
    [InlineData("space name.txt")]
    public void UnsafePaths_AreRejected(string path) =>
        Assert.Equal(TemplateError.InvalidTemplatePath, Fails(() => TemplateContent.Normalize([new TemplateFile(path, "x")])).Error);

    [Fact]
    public void BinaryLikeContent_EmptyTemplates_AndCaseInsensitiveDuplicates_AreRejected()
    {
        Assert.Equal(TemplateError.InvalidTemplateContent, Fails(() => TemplateContent.Normalize([new TemplateFile("a.txt", "x\0y")])).Error);
        Assert.Equal(TemplateError.TemplateTooLarge, Fails(() => TemplateContent.Normalize([])).Error);
        Assert.Equal(TemplateError.InvalidTemplatePath,
            Fails(() => TemplateContent.Normalize([new TemplateFile("A.txt", "1"), new TemplateFile("a.txt", "2")])).Error);
        Assert.Equal(TemplateError.TemplateTooLarge,
            Fails(() => TemplateContent.Normalize([new TemplateFile("big.txt", new string('x', TemplateContent.MaxBytes + 1))])).Error);
    }

    [Fact]
    public void Catalog_RefusesUnknownTemplates_InvalidRegistries_AndChangedFiles()
    {
        var catalog = new FileSystemTemplateCatalog(TemplateRepository.Root());
        Assert.Equal(TemplateError.TemplateNotFound, Fails(() => catalog.Load(TemplateRepository.Id, "9.9.9")).Error);

        var root = TemplateRepository.NewTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "registry.json"),
                """{"templates":[{"id":"x","version":"1","directory":"..","token":"T","kebabToken":"t","contentSha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""");
            Assert.Equal(TemplateError.InvalidRegistry, Fails(() => new FileSystemTemplateCatalog(root).List()).Error);
            File.WriteAllText(Path.Combine(root, "registry.json"), "not json");
            Assert.Equal(TemplateError.InvalidRegistry, Fails(() => new FileSystemTemplateCatalog(root).List()).Error);

            // A file added to a registered template directory changes the hash and is refused.
            var copy = Path.Combine(root, "starter-v1");
            foreach (var file in Starter.Files)
            {
                var target = Path.Combine(copy, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, file.Content);
            }
            File.Copy(Path.Combine(TemplateRepository.Root(), "registry.json"), Path.Combine(root, "registry.json"), overwrite: true);
            Assert.Equal(Starter.Template, new FileSystemTemplateCatalog(root).Load(TemplateRepository.Id, TemplateRepository.Version).Template);
            Directory.CreateDirectory(Path.Combine(copy, "obj"));
            File.WriteAllText(Path.Combine(copy, "obj", "ignored.txt"), "build output is not template content");
            Assert.Equal(Starter.Template, new FileSystemTemplateCatalog(root).Load(TemplateRepository.Id, TemplateRepository.Version).Template);
            File.WriteAllText(Path.Combine(copy, "src", "Injected.cs"), "class Injected {}");
            Assert.Equal(TemplateError.TemplateHashMismatch,
                Fails(() => new FileSystemTemplateCatalog(root).Load(TemplateRepository.Id, TemplateRepository.Version)).Error);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Writer_WritesExactBytes_IntoAnEmptyDirectoryOnly()
    {
        var application = StarterGenerator.Generate(Starter.Template, Starter.Files, ApplicationName.Create("StockFlow"));
        var root = TemplateRepository.NewTempDirectory();
        try
        {
            var target = Path.Combine(root, "app");
            GeneratedApplicationWriter.Write(application, target);
            var written = TemplateContent.Normalize(FileSystemTemplateCatalog.Read(target));
            Assert.Equal(application.ManifestSha256, TemplateContent.Hash(written));
            Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(Path.Combine(target, "src", "StockFlow.Api", "Program.cs")));
            Assert.Throws<IOException>(() => GeneratedApplicationWriter.Write(application, target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FACTORY_DOCKER_TESTS") != "1")
            Skip = "Requires Docker (set FACTORY_DOCKER_TESTS=1).";
    }
}

// M9.1 evidence: a generated application restores, builds and passes its own tests inside pinned
// containers. Package restore uses the network because the template is Factory-authored and trusted;
// untrusted generated code runs only in the offline M9.2 sandbox.
[Collection(DockerCollection.Name)]
public class StarterTemplateContainerTests
{
    public const string DotnetSdkImage = "mcr.microsoft.com/dotnet/sdk@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa";
    public const string NodeImage = "node@sha256:a5f4366e834695d5f363b920c104953a535d3a9864d1bdf74566a0ebe01daee5";

    [DockerFact]
    public async Task GeneratedApplication_BuildsAndPassesItsTests_InPinnedContainers()
    {
        var (template, files) = new FileSystemTemplateCatalog(TemplateRepository.Root()).Load(TemplateRepository.Id, TemplateRepository.Version);
        var application = StarterGenerator.Generate(template, files, ApplicationName.Create("SampleShop"));
        var root = TemplateRepository.NewTempDirectory();
        try
        {
            GeneratedApplicationWriter.Write(application, root);
            var dotnet = Run(root, DotnetSdkImage, "dotnet test SampleShop.slnx");
            var web = Run(Path.Combine(root, "web"), NodeImage,
                "npm ci --ignore-scripts --no-audit --no-fund && npx ng test --watch=false && npx ng build");
            var results = await Task.WhenAll(dotnet, web);
            Assert.True(results[0].Exit == 0, results[0].Output);
            Assert.Contains("Passed!", results[0].Output);
            Assert.True(results[1].Exit == 0, results[1].Output);
            Assert.Contains("Application bundle generation complete", results[1].Output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<(int Exit, string Output)> Run(string directory, string image, string command)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "run", "--rm", "-v", directory + ":/work", "-w", "/work", image, "sh", "-c", command })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = new StringBuilder();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await process.WaitForExitAsync(timeout.Token);
        output.Append(await stdout).Append(await stderr);
        var text = output.ToString();
        return (process.ExitCode, text.Length > 4000 ? text[^4000..] : text);
    }
}
