using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Core.Templates;
using Factory.Infrastructure.Execution;
using Factory.Infrastructure.Templates;

namespace Factory.Tests.Templates;

public class DevelopmentWorkspaceTests
{
    internal static GeneratedApplication Application(string name = "StockFlow")
    {
        var (template, files) = new FileSystemTemplateCatalog(TemplateRepository.Root()).Load(TemplateRepository.Id, TemplateRepository.Version);
        return StarterGenerator.Generate(template, files, ApplicationName.Create(name));
    }

    private static readonly string[] Req = ["FR-1"];
    private static WorkspaceError Fails(Action action) => Assert.Throws<WorkspaceException>(action).Error;

    [Fact]
    public void ReadsListsAndSearchesTheAuthoritativeFiles()
    {
        var workspace = new DevelopmentWorkspace(Application());
        Assert.Contains("namespace StockFlow.Domain.Common;", workspace.Read("src/StockFlow.Domain/Common/Entity.cs"));
        Assert.Equal(["src/StockFlow.Domain/Common/AggregateRoot.cs", "src/StockFlow.Domain/Common/DomainException.cs",
            "src/StockFlow.Domain/Common/Entity.cs", "src/StockFlow.Domain/Common/IDomainEvent.cs", "src/StockFlow.Domain/StockFlow.Domain.csproj"],
            workspace.List("src/StockFlow.Domain"));
        Assert.Contains("src/StockFlow.Domain/Common/AggregateRoot.cs:5: public abstract class AggregateRoot<TId> : Entity<TId> where TId : notnull",
            workspace.Search("class AggregateRoot"));
        Assert.Equal(WorkspaceError.NotFound, Fails(() => workspace.Read("src/Missing.cs")));
        Assert.Equal(WorkspaceError.InvalidContent, Fails(() => workspace.Search("")));
        Assert.Equal(workspace.BaseManifestSha256, workspace.ManifestSha256);
        Assert.Empty(workspace.Changes());
    }

    [Fact]
    public void EditsAreTrackedWithTheirRequirements_AndRevertingRemovesTheChange()
    {
        var application = Application();
        var workspace = new DevelopmentWorkspace(application);
        workspace.Write("src/StockFlow.Domain/Products/Product.cs", "namespace StockFlow.Domain.Products;\r\npublic sealed class Product { }\r\n", ["FR-1"]);
        workspace.Write("src/StockFlow.Domain/Products/Product.cs", "namespace StockFlow.Domain.Products;\npublic sealed class Product { }\n", ["FR-2"]);
        var original = workspace.Read("src/StockFlow.Api/Program.cs");
        workspace.Write("src/StockFlow.Api/Program.cs", original + "// products\n", ["FR-1"]);
        workspace.Delete("src/StockFlow.Domain/Common/DomainException.cs", ["FR-3"]);
        Assert.Equal("namespace StockFlow.Domain.Products;\npublic sealed class Product { }\n", workspace.Read("src/StockFlow.Domain/Products/Product.cs"));
        Assert.Equal(
        [
            new FileChange("src/StockFlow.Api/Program.cs", FileChangeKind.Modified, ["FR-1"]),
            new FileChange("src/StockFlow.Domain/Common/DomainException.cs", FileChangeKind.Deleted, ["FR-3"]),
            new FileChange("src/StockFlow.Domain/Products/Product.cs", FileChangeKind.Added, ["FR-1", "FR-2"]),
        ], workspace.Changes(), new ChangeComparer());
        Assert.NotEqual(workspace.BaseManifestSha256, workspace.ManifestSha256);
        workspace.Write("src/StockFlow.Api/Program.cs", original, ["FR-1"]);
        Assert.DoesNotContain(workspace.Changes(), c => c.Path == "src/StockFlow.Api/Program.cs");
        Assert.Equal(application.Files.Count, workspace.BaseFiles.Count);
    }

    private sealed class ChangeComparer : IEqualityComparer<FileChange>
    {
        public bool Equals(FileChange? x, FileChange? y) => x!.Path == y!.Path && x.Kind == y.Kind && x.RequirementIds.SequenceEqual(y.RequirementIds);
        public int GetHashCode(FileChange obj) => obj.Path.GetHashCode();
    }

    [Theory]
    [InlineData("tests/StockFlow.Tests/ArchitectureTests.cs")]
    [InlineData("src/StockFlow.Domain/StockFlow.Domain.csproj")]
    [InlineData("src/StockFlow.Domain/New.csproj")]
    [InlineData("StockFlow.slnx")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("global.json")]
    [InlineData("nuget.config")]
    [InlineData(".gitattributes")]
    [InlineData("src/.gitignore")]
    [InlineData("web/package.json")]
    [InlineData("web/package-lock.json")]
    [InlineData("web/angular.json")]
    [InlineData("web/tsconfig.app.json")]
    [InlineData("web/.npmrc")]
    public void TheRulesTheFactoryEnforces_CannotBeEdited(string path)
    {
        var workspace = new DevelopmentWorkspace(Application());
        Assert.Equal(WorkspaceError.ProtectedPath, Fails(() => workspace.Write(path, "x", Req)));
        if (workspace.List().Contains(path)) Assert.Equal(WorkspaceError.ProtectedPath, Fails(() => workspace.Delete(path, Req)));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("/etc/passwd")]
    [InlineData(".git/config")]
    [InlineData("src/.git/hooks/pre-commit")]
    [InlineData("src/StockFlow.Api/bin/x.dll")]
    [InlineData("web/node_modules/x/index.js")]
    [InlineData("web/dist/main.js")]
    [InlineData("a\\b.cs")]
    [InlineData("src//x.cs")]
    public void UnsafePaths_AreRejected(string path)
    {
        var workspace = new DevelopmentWorkspace(Application());
        Assert.Equal(WorkspaceError.InvalidPath, Fails(() => workspace.Write(path, "x", Req)));
        Assert.Equal(WorkspaceError.InvalidPath, Fails(() => workspace.Read(path)));
    }

    [Fact]
    public void EditsNeedRequirements_TextContent_AndStayWithinLimits()
    {
        var workspace = new DevelopmentWorkspace(Application());
        Assert.Equal(WorkspaceError.MissingRequirement, Fails(() => workspace.Write("src/A.cs", "x", [])));
        Assert.Equal(WorkspaceError.MissingRequirement, Fails(() => workspace.Write("src/A.cs", "x", ["has space"])));
        Assert.Equal(WorkspaceError.MissingRequirement, Fails(() => workspace.Delete("src/StockFlow.Api/Program.cs", [])));
        Assert.Equal(WorkspaceError.InvalidContent, Fails(() => workspace.Write("src/A.cs", "a\0b", Req)));
        Assert.Equal(WorkspaceError.TooLarge, Fails(() => workspace.Write("src/A.cs", new string('x', WorkspaceRules.MaxFileBytes + 1), Req)));
        Assert.Equal(WorkspaceError.InvalidPath, Fails(() => workspace.Write("src/stockflow.api/program.cs", "x", Req))); // case-insensitive duplicate
        Assert.Equal(WorkspaceError.NotFound, Fails(() => workspace.Delete("src/Missing.cs", Req)));
        Assert.Empty(workspace.Changes());
    }
}

// Executed against the real runner image.
[Collection(DockerCollection.Name)]
public class DevelopmentSandboxTests
{
    private static readonly string Image = Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE") ?? "";
    private static DockerSandboxOptions Options(SandboxLimits? limits = null) => new(Image, limits ?? SandboxLimits.Default);

    [SandboxFact]
    public async Task ASession_BuildsAndTestsAgentEdits_AndTamperingNeverReachesTheDiff()
    {
        var workspace = new DevelopmentWorkspace(DevelopmentWorkspaceTests.Application("SampleShop"));
        const string product = """
            using SampleShop.Domain.Common;

            namespace SampleShop.Domain.Products;

            public sealed class Product : AggregateRoot<Guid>
            {
                private Product(Guid id, string name) : base(id) => Name = name;

                public string Name { get; private set; }

                public static Product Create(Guid id, string name) =>
                    string.IsNullOrWhiteSpace(name) ? throw new DomainException("product_name_required") : new Product(id, name.Trim());
            }

            """;
        const string tests = """
            using SampleShop.Domain.Common;
            using SampleShop.Domain.Products;

            namespace SampleShop.Tests;

            public class ProductTests
            {
                [Fact]
                public void ANameIsRequired() => Assert.Equal("product_name_required", Assert.Throws<DomainException>(() => Product.Create(Guid.NewGuid(), " ")).Code);
            }

            """;
        workspace.Write("src/SampleShop.Domain/Products/Product.cs", product, ["FR-1"]);
        workspace.Write("tests/SampleShop.Tests/ProductTests.cs", tests, ["FR-1"]);
        await using (var session = await DockerWorkspaceSession.StartAsync(Options(), workspace.BaseFiles))
        {
            foreach (var change in workspace.Changes()) await session.WriteAsync(change.Path, workspace.Read(change.Path));
            var steps = StarterVerification.Steps(workspace.Name);
            foreach (var step in steps.Take(3))
            {
                var result = await session.RunAsync(step);
                Assert.True(result.Outcome == StepOutcome.Passed, result.Output);
            }
            Assert.Contains("Passed!", (await session.RunAsync(steps[2])).Output);
            // Untrusted code rewrites a source file inside the sandbox; the authoritative workspace is unchanged.
            Assert.Equal(StepOutcome.Passed, (await session.RunAsync(new SandboxStep("tamper", ["sh", "-c",
                "echo TAMPERED > /work/src/SampleShop.Domain/Common/Entity.cs"]))).Outcome);
            await session.DeleteAsync("tests/SampleShop.Tests/ProductTests.cs");
            Assert.Equal(StepOutcome.Failed, (await session.RunAsync(new SandboxStep("gone", ["test", "-e", "/work/tests/SampleShop.Tests/ProductTests.cs"]))).Outcome);
        }
        var diff = await GitSnapshot.CreateAsync(Options(), workspace.BaseFiles, workspace.Files);
        Assert.Contains("+++ b/src/SampleShop.Domain/Products/Product.cs", diff.Diff);
        Assert.Contains("+++ b/tests/SampleShop.Tests/ProductTests.cs", diff.Diff);
        Assert.Contains("+    public static Product Create(Guid id, string name) =>", diff.Diff);
        Assert.DoesNotContain("TAMPERED", diff.Diff);
        Assert.DoesNotContain("Entity.cs", diff.Diff);
        Assert.Equal(2, diff.Diff.Split('\n').Count(l => l.StartsWith("diff --git ", StringComparison.Ordinal)));
    }

    [SandboxFact]
    public async Task Snapshots_AreDeterministic_AndDependOnlyOnContent()
    {
        var workspace = new DevelopmentWorkspace(DevelopmentWorkspaceTests.Application("SampleShop"));
        workspace.Write("src/SampleShop.Domain/Note.cs", "namespace SampleShop.Domain;\n", ["FR-1"]);
        var first = await GitSnapshot.CreateAsync(Options(), workspace.BaseFiles, workspace.Files);
        var second = await GitSnapshot.CreateAsync(Options(), workspace.BaseFiles, workspace.Files);
        Assert.Equal((first.BaseCommit, first.FinalCommit, first.DiffSha256), (second.BaseCommit, second.FinalCommit, second.DiffSha256));
        var unchanged = await GitSnapshot.CreateAsync(Options(), workspace.BaseFiles, workspace.BaseFiles);
        Assert.Equal(first.BaseCommit, unchanged.BaseCommit);
        Assert.Equal("", unchanged.Diff);
        Assert.NotEqual(first.FinalCommit, unchanged.FinalCommit);
    }

    [SandboxFact]
    public async Task ATimedOutStep_FaultsTheSession()
    {
        var limits = SandboxLimits.Default with { StepTimeout = TimeSpan.FromSeconds(3), JobTimeout = TimeSpan.FromSeconds(30) };
        await using var session = await DockerWorkspaceSession.StartAsync(Options(limits), [new TemplateFile("a.txt", "a")]);
        Assert.Equal(StepOutcome.TimedOut, (await session.RunAsync(new SandboxStep("hang", ["sleep", "60"]))).Outcome);
        Assert.True(session.Faulted);
        Assert.Equal("sandbox_faulted", (await Assert.ThrowsAsync<SandboxUnavailableException>(() => session.RunAsync(new SandboxStep("next", ["true"])))).Code);
    }
}
