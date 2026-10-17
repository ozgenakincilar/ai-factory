using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using Factory.Core.Deployment;
using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Infrastructure.Deployment;
using Factory.Infrastructure.Execution;
using Factory.Tests.Templates;

namespace Factory.Tests.Deployment;

internal static class DeploymentSamples
{
    internal const string Subscription = "11111111-2222-3333-4444-555555555555";
    internal const string Tenant = "66666666-7777-8888-9999-000000000000";

    internal static DeploymentTarget Target(string resourceGroup = "rg-stock-flow-staging-frc") => new("azure", "staging", "stock-flow", Subscription, Tenant,
        "francecentral", resourceGroup, "0a1b2c3d", "rg-factory-tfstate-frc", "stfactorytf111111112222", "tfstate-stock-flow-staging", "stock-flow-staging.tfstate");

    internal static DeploymentModule Module => new("azure-app-service-postgres", "1.0.0", "azure/app-service-postgres", new string('c', 64));

    internal static ReleasePackage Release(byte[]? zip = null)
    {
        zip ??= [1, 2, 3, 4];
        return new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "stock-flow", new string('f', 40), ReleasePackage.Hash(zip), zip);
    }

    internal static DeploymentPlan Plan(byte[] planFile, PlanSummary? summary = null, DeploymentTarget? target = null, ReleasePackage? release = null)
    {
        var binding = DeploymentRules.Bind(target ?? Target(), Module, release ?? Release(), planFile);
        return new(binding, binding.Hash(), planFile, summary ?? new PlanSummary(7, 0, 0, []), "");
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !(File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(directory.FullName, "deploy"))))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

public class DeploymentRuleTests
{
    [Fact]
    public void Target_IsValidated()
    {
        DeploymentSamples.Target().Validate();
        Assert.Throws<DeploymentException>(() => (DeploymentSamples.Target() with { Environment = "production" }).Validate());
        Assert.Throws<DeploymentException>(() => (DeploymentSamples.Target() with { SubscriptionId = "not-a-guid" }).Validate());
        Assert.Throws<DeploymentException>(() => (DeploymentSamples.Target() with { ResourceGroup = "RG With Spaces" }).Validate());
        Assert.Throws<DeploymentException>(() => (DeploymentSamples.Target() with { StateKey = "../other.tfstate" }).Validate());
    }

    [Fact]
    public void Inputs_FollowFromTargetAndRelease_AndNameTheReleaseByItsHash()
    {
        var release = DeploymentSamples.Release();
        var inputs = DeploymentRules.Inputs(DeploymentSamples.Target(), release);
        Assert.Equal("release/" + release.Sha256 + ".zip", inputs["release_package"]);
        Assert.Equal(release.Sha256, inputs["release_sha256"]);
        Assert.Equal(["application_name", "environment", "location", "name_suffix", "release_package", "release_sha256", "resource_group_name"], inputs.Keys);
        Assert.Equal(DeploymentRules.InputsHash(inputs), DeploymentRules.InputsHash(DeploymentRules.Inputs(DeploymentSamples.Target(), release)));
    }

    [Fact]
    public void Binding_ChangesWithEveryBoundValue()
    {
        var plan = Encoding.UTF8.GetBytes("plan");
        var target = DeploymentSamples.Target();
        var release = DeploymentSamples.Release();
        var hash = DeploymentRules.Bind(target, DeploymentSamples.Module, release, plan).Hash();
        Assert.Equal(hash, DeploymentRules.Bind(target, DeploymentSamples.Module, release, plan).Hash());
        string[] others =
        [
            DeploymentRules.Bind(target, DeploymentSamples.Module, release, Encoding.UTF8.GetBytes("plan2")).Hash(),
            DeploymentRules.Bind(DeploymentSamples.Target("rg-other"), DeploymentSamples.Module, release, plan).Hash(),
            DeploymentRules.Bind(target, DeploymentSamples.Module with { Version = "1.0.1" }, release, plan).Hash(),
            DeploymentRules.Bind(target, DeploymentSamples.Module with { ContentSha256 = new string('d', 64) }, release, plan).Hash(),
            DeploymentRules.Bind(target, DeploymentSamples.Module, DeploymentSamples.Release([9]), plan).Hash(),
            DeploymentRules.Bind(target, DeploymentSamples.Module, release with { SourceCommit = new string('e', 40) }, plan).Hash(),
            DeploymentRules.Bind(target, DeploymentSamples.Module, release with { DevelopmentJobId = Guid.NewGuid() }, plan).Hash(),
        ];
        Assert.All(others, h => Assert.NotEqual(hash, h));
        Assert.Equal(others.Length, others.Distinct().Count());
    }

    [Fact]
    public void Verify_AcceptsOnlyTheApprovedUnchangedPlan()
    {
        var plan = DeploymentSamples.Plan(Encoding.UTF8.GetBytes("plan"));
        var approval = new DeploymentApproval(plan.BindingSha256, "owner@example.test", DateTimeOffset.UtcNow);
        DeploymentRules.Verify(DeploymentSamples.Target(), DeploymentSamples.Module, DeploymentSamples.Release(), plan, approval);

        DeploymentError Error(Action action) => Assert.Throws<DeploymentException>(action).Error;
        // Tampered plan bytes, another target or release: the binding no longer matches.
        Assert.Equal(DeploymentError.PlanTampered, Error(() => DeploymentRules.Verify(DeploymentSamples.Target(), DeploymentSamples.Module,
            DeploymentSamples.Release(), plan with { PlanFile = Encoding.UTF8.GetBytes("plan!") }, approval)));
        Assert.Equal(DeploymentError.PlanTampered, Error(() => DeploymentRules.Verify(DeploymentSamples.Target("rg-other"), DeploymentSamples.Module,
            DeploymentSamples.Release(), plan, approval)));
        Assert.Equal(DeploymentError.PlanTampered, Error(() => DeploymentRules.Verify(DeploymentSamples.Target(), DeploymentSamples.Module,
            DeploymentSamples.Release([5, 6]), plan, approval)));
        Assert.Equal(DeploymentError.PlanTampered, Error(() => DeploymentRules.Verify(DeploymentSamples.Target(), DeploymentSamples.Module,
            DeploymentSamples.Release(), plan with { BindingSha256 = new string('0', 64) }, approval with { BindingSha256 = new string('0', 64) })));
        // An approval of another plan, or without an approver.
        Assert.Equal(DeploymentError.ApprovalMismatch, Error(() => DeploymentRules.Verify(DeploymentSamples.Target(), DeploymentSamples.Module,
            DeploymentSamples.Release(), plan, approval with { BindingSha256 = new string('1', 64) })));
        Assert.Equal(DeploymentError.ApprovalMismatch, Error(() => DeploymentRules.Verify(DeploymentSamples.Target(), DeploymentSamples.Module,
            DeploymentSamples.Release(), plan, approval with { ApprovedBy = " " })));
    }

    [Fact]
    public void DestructivePlans_NeedAnExplicitDestructiveApproval()
    {
        var plan = DeploymentSamples.Plan(Encoding.UTF8.GetBytes("plan"), new PlanSummary(1, 0, 1,
            [new("azurerm_linux_web_app.api", "azurerm_linux_web_app", ["delete", "create"])]));
        var approval = new DeploymentApproval(plan.BindingSha256, "owner@example.test", DateTimeOffset.UtcNow);
        Assert.Equal(DeploymentError.DestructivePlan, Assert.Throws<DeploymentException>(() =>
            DeploymentRules.Verify(DeploymentSamples.Target(), DeploymentSamples.Module, DeploymentSamples.Release(), plan, approval)).Error);
        DeploymentRules.Verify(DeploymentSamples.Target(), DeploymentSamples.Module, DeploymentSamples.Release(), plan, approval with { AllowDestructive = true });
    }

    [Fact]
    public void PlanSummary_CountsCreatesUpdatesAndDeletes_IgnoringReadsAndNoOps()
    {
        const string json = """
        {"format_version":"1.2","resource_changes":[
          {"address":"azurerm_service_plan.application","type":"azurerm_service_plan","change":{"actions":["create"]}},
          {"address":"azurerm_linux_web_app.api","type":"azurerm_linux_web_app","change":{"actions":["update"]}},
          {"address":"azurerm_key_vault.secrets","type":"azurerm_key_vault","change":{"actions":["delete","create"]}},
          {"address":"data.azurerm_resource_group.target","type":"azurerm_resource_group","change":{"actions":["read"]}},
          {"address":"azurerm_postgresql_flexible_server.database","type":"azurerm_postgresql_flexible_server","change":{"actions":["no-op"]}}]}
        """;
        var summary = PlanSummary.FromTerraformJson(json);
        Assert.Equal((2, 1, 1, 3, true), (summary.Add, summary.Change, summary.Destroy, summary.Changes.Count, summary.Destructive));
        Assert.Equal(DeploymentError.PlanFailed, Assert.Throws<DeploymentException>(() => PlanSummary.FromTerraformJson("{\"resource_changes\":[{}]}")).Error);
        Assert.Equal(DeploymentError.PlanFailed, Assert.Throws<DeploymentException>(() => PlanSummary.FromTerraformJson("not json")).Error);
    }

    [Fact]
    public void ReleasePackage_HashMustMatchItsBytes()
    {
        DeploymentSamples.Release().Validate();
        Assert.Throws<DeploymentException>(() => (DeploymentSamples.Release() with { Sha256 = new string('0', 64) }).Validate());
        Assert.Throws<DeploymentException>(() => (DeploymentSamples.Release() with { SourceCommit = "abc" }).Validate());
    }
}

public class ReleaseArchiveTests
{
    private static byte[] Tar(params (string Name, TarEntryType Type, string? Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
            foreach (var (name, type, content) in entries)
            {
                var entry = new PaxTarEntry(type, name);
                if (type == TarEntryType.SymbolicLink) entry.LinkName = "/etc/passwd";
                if (content is not null) entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                writer.WriteEntry(entry);
            }
        return buffer.ToArray();
    }

    [Fact]
    public void Zip_IsDeterministic_AndHoldsTheRegularFiles()
    {
        var tar = Tar(("./", TarEntryType.Directory, null), ("./b.dll", TarEntryType.RegularFile, "b"), ("./runtimes/", TarEntryType.Directory, null),
            ("./runtimes/linux-x64/native/a.so", TarEntryType.RegularFile, "a"), ("./appsettings.json", TarEntryType.RegularFile, "{}"));
        var zip = ReleaseArchive.FromTar(tar);
        Assert.Equal(zip, ReleaseArchive.FromTar(tar));
        using var archive = new ZipArchive(new MemoryStream(zip));
        Assert.Equal(["appsettings.json", "b.dll", "runtimes/linux-x64/native/a.so"], archive.Entries.Select(e => e.FullName));
    }

    [Theory]
    [InlineData("../escape.dll", TarEntryType.RegularFile)]
    [InlineData("/etc/cron.d/x", TarEntryType.RegularFile)]
    [InlineData("./link", TarEntryType.SymbolicLink)]
    [InlineData("./dir/../../x", TarEntryType.RegularFile)]
    public void UnsafeEntries_AreRefused(string name, TarEntryType type) =>
        Assert.Throws<DeploymentException>(() => ReleaseArchive.FromTar(Tar(("./ok.dll", TarEntryType.RegularFile, "x"),
            (name, type, type == TarEntryType.RegularFile ? "x" : null))));

    [Fact]
    public void EmptyOutput_IsRefused() => Assert.Throws<DeploymentException>(() => ReleaseArchive.FromTar(Tar(("./", TarEntryType.Directory, null))));
}

public class DeploymentModuleCatalogTests
{
    [Fact]
    public void TheCommittedModule_MatchesItsRegisteredHash()
    {
        var catalog = new FileSystemModuleCatalog(Path.Combine(DeploymentSamples.RepositoryRoot(), "deploy"));
        var (module, files) = catalog.Load("azure-app-service-postgres", "1.0.0");
        Assert.Equal("azure/app-service-postgres", module.Directory);
        Assert.Contains(files, f => f.Path == ".terraform.lock.hcl");
        var versions = files.Single(f => f.Path == "versions.tf").Content;
        // Pinned tool and providers; the provider lock file pins their hashes.
        Assert.Contains("required_version = \"= 1.16.5\"", versions);
        Assert.Contains("version = \"= 5.8.0\"", versions);
        Assert.Contains("use_azuread_auth = true", versions);
        var main = files.Single(f => f.Path == "main.tf").Content;
        // The database password is write-only and never part of a plan or the state.
        Assert.Contains("administrator_password_wo ", main);
        Assert.DoesNotContain("administrator_password ", main);
        Assert.Contains("\"F1\"", main);
        Assert.Contains("\"B_Standard_B1ms\"", main);
    }

    [Fact]
    public void Version110_ResolvesKeyVaultReferencesWithAnIdentityGrantedBeforeTheApp()
    {
        var catalog = new FileSystemModuleCatalog(Path.Combine(DeploymentSamples.RepositoryRoot(), "deploy"));
        var (module, files) = catalog.Load("azure-app-service-postgres", "1.1.0");
        Assert.Equal("azure/app-service-postgres-1.1.0", module.Directory);
        var main = files.Single(f => f.Path == "main.tf").Content;
        Assert.Contains("key_vault_reference_identity_id = azurerm_user_assigned_identity.api.id", main);
        Assert.Contains("depends_on = [azurerm_key_vault_access_policy.api_identity]", main);
        // An upgrade from 1.0.0 keeps every 1.0.0 resource, so it destroys nothing.
        var (_, previous) = catalog.Load("azure-app-service-postgres", "1.0.0");
        foreach (var resource in System.Text.RegularExpressions.Regex.Matches(previous.Single(f => f.Path == "main.tf").Content, "resource \"[a-z_]+\" \"[a-z_]+\""))
            Assert.Contains(resource.ToString()!, main);
    }

    [Fact]
    public void ChangedModules_AreRefused()
    {
        var root = Directory.CreateTempSubdirectory("factory-modules-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "m"));
            File.WriteAllText(Path.Combine(root.FullName, "m", "main.tf"), "# changed\n");
            File.WriteAllText(Path.Combine(root.FullName, "registry.json"),
                "{\"modules\":[{\"id\":\"m\",\"version\":\"1.0.0\",\"directory\":\"m\",\"contentSha256\":\"" + new string('a', 64) + "\"}]}");
            var catalog = new FileSystemModuleCatalog(root.FullName);
            Assert.Equal(DeploymentError.ModuleHashMismatch, Assert.Throws<DeploymentException>(() => catalog.Load("m", "1.0.0")).Error);
            Assert.Equal(DeploymentError.InvalidModule, Assert.Throws<DeploymentException>(() => catalog.Load("m", "2.0.0")).Error);
        }
        finally { root.Delete(true); }
    }
}

public class TerraformEngineTests
{
    private const string Image = "hashicorp/terraform@sha256:c7926feace05d0f7e73542842bf3945924e955a1f782cf000ccbb8d18fa42d77";
    private const string Secret = "super-secret-value-1234567890";

    private static TerraformDeploymentEngine Engine(string tenant = DeploymentSamples.Tenant) => new(
        new TerraformOptions(Image, Path.Combine(Path.GetTempPath(), "factory-tf-plugins-test"),
            new AzureCredentials(tenant, DeploymentSamples.Subscription, "client-id", Secret)),
        new FileSystemModuleCatalog(Path.Combine(DeploymentSamples.RepositoryRoot(), "deploy")));

    [Fact]
    public void ContainerArguments_AreConstrained_AndCarryNoSecret()
    {
        var arguments = Engine().RunArguments("factory-tf-x", "C:/work", ["plan"]);
        Assert.DoesNotContain(arguments, a => a.Contains(Secret, StringComparison.Ordinal));
        // Credentials are forwarded by name from the client process environment.
        Assert.Contains("ARM_CLIENT_SECRET", arguments);
        foreach (var expected in new[] { "--read-only", "--rm", "--init", "no-new-privileges", "ALL", "--pids-limit", "--memory" }) Assert.Contains(expected, arguments);
        Assert.Equal(["10001:10001"], arguments.SkipWhile(a => a != "--user").Skip(1).Take(1));
        Assert.Equal(Image, arguments[^2]);
        Assert.Equal("plan", arguments[^1]);
    }

    [Fact]
    public void Credentials_AreNeverPrinted() =>
        Assert.DoesNotContain(Secret, new AzureCredentials(DeploymentSamples.Tenant, DeploymentSamples.Subscription, "client-id", Secret).ToString());

    [Fact]
    public void UnpinnedImages_AreRefused() => Assert.Throws<ArgumentException>(() => new TerraformDeploymentEngine(
        new TerraformOptions("hashicorp/terraform:latest", "x", new AzureCredentials(DeploymentSamples.Tenant, DeploymentSamples.Subscription, "c", "s")),
        new FileSystemModuleCatalog("x")));

    [Fact]
    public async Task CredentialsOfAnotherTenant_AreRefusedBeforeAnythingRuns()
    {
        var error = await Assert.ThrowsAsync<DeploymentException>(() =>
            Engine(tenant: "99999999-7777-8888-9999-000000000000").PlanAsync(DeploymentSamples.Target(), DeploymentSamples.Module, DeploymentSamples.Release()));
        Assert.Equal((DeploymentError.InvalidTarget, "credentials_target_mismatch"), (error.Error, error.Detail));
    }

    [Fact]
    public async Task Apply_RefusesAPlanThatWasNotApproved()
    {
        var plan = DeploymentSamples.Plan(Encoding.UTF8.GetBytes("plan"));
        var error = await Assert.ThrowsAsync<DeploymentException>(() => Engine().ApplyAsync(DeploymentSamples.Target(), DeploymentSamples.Module,
            DeploymentSamples.Release(), plan, new DeploymentApproval(new string('9', 64), "owner@example.test", DateTimeOffset.UtcNow)));
        Assert.Equal(DeploymentError.ApprovalMismatch, error.Error);
    }
}

public class HealthCheckerTests
{
    private sealed class Responses(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        public int Calls;
        public List<Uri> Uris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            var (status, body) = responses[Math.Min(Calls++, responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task HealthyAfterAColdStart()
    {
        var handler = new Responses((HttpStatusCode.ServiceUnavailable, ""), (HttpStatusCode.OK, "{\"status\":\"ok\"}"));
        var result = await new HttpHealthChecker(new HttpClient(handler), 5, TimeSpan.Zero).CheckAsync("https://stock-flow-staging-0a1b2c3d.azurewebsites.net");
        Assert.Equal((true, 200, 2), (result.Healthy, result.StatusCode, result.Attempts));
        Assert.Equal("https://stock-flow-staging-0a1b2c3d.azurewebsites.net/health", handler.Uris[0].ToString());
    }

    [Fact]
    public async Task UnhealthyWhenTheBodyIsNotOk()
    {
        var result = await new HttpHealthChecker(new HttpClient(new Responses((HttpStatusCode.OK, "{\"status\":\"degraded\"}"))), 3, TimeSpan.Zero)
            .CheckAsync("https://x.azurewebsites.net");
        Assert.Equal((false, 200, 3), (result.Healthy, result.StatusCode, result.Attempts));
    }

    [Fact]
    public async Task PlainHttp_IsRefused() =>
        await Assert.ThrowsAsync<DeploymentException>(() => new HttpHealthChecker(new HttpClient(new Responses((HttpStatusCode.OK, "")))).CheckAsync("http://x.test"));
}

[Collection(DockerCollection.Name)]
public class ReleaseBuilderTests
{
    [SandboxFact]
    public async Task VerifiedSource_IsRebuiltFromTheEvidenceCommits_AndPublished()
    {
        var options = new DockerSandboxOptions(Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE")!, SandboxLimits.Default);
        var application = DevelopmentWorkspaceTests.Application("SampleShop");
        var workspace = new DevelopmentWorkspace(application);
        workspace.Write("src/SampleShop.Domain/Note.cs", "namespace SampleShop.Domain;\n\npublic sealed record Note(string Text);\n", ["FR-1"]);
        var diff = await GitSnapshot.CreateAsync(options, workspace.BaseFiles, workspace.Files);
        var job = Guid.NewGuid();
        var evidence = PullRequestSamples.Evidence(job, diff.BaseCommit, diff.FinalCommit, diff.Diff, workspace.Changes());
        var builder = new SandboxReleaseBuilder(options);

        var release = await builder.BuildAsync(job, evidence, application.Files);
        Assert.Equal((job, "sample-shop", diff.FinalCommit), (release.DevelopmentJobId, release.ApplicationName, release.SourceCommit));
        release.Validate();
        using (var archive = new ZipArchive(new MemoryStream(release.Zip)))
        {
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("SampleShop.Api.dll", names);
            Assert.Contains("SampleShop.Domain.dll", names);
            Assert.Contains("SampleShop.Api.runtimeconfig.json", names);
            Assert.DoesNotContain(names, n => n.Contains("factory-release.diff", StringComparison.Ordinal) || n.StartsWith(".git", StringComparison.Ordinal));
        }

        // A diff that does not reproduce the evidence's final commit is refused before anything is built.
        var tampered = evidence with { Diff = diff.Diff!.Replace("Note(string Text)", "Note(string Other)", StringComparison.Ordinal) };
        var error = await Assert.ThrowsAsync<DeploymentException>(() => builder.BuildAsync(job, tampered, application.Files));
        Assert.Equal((DeploymentError.ReleaseCommitMismatch, "final_commit_mismatch"), (error.Error, error.Detail));
        // Only verified evidence of this job.
        await Assert.ThrowsAsync<DeploymentException>(() => builder.BuildAsync(Guid.NewGuid(), evidence, application.Files));
        await Assert.ThrowsAsync<DeploymentException>(() => builder.BuildAsync(job, evidence with { Status = DevelopmentJobStatus.VerificationFailed }, application.Files));
    }
}

public class ConfigurationCheckerTests
{
    private sealed class FakeAzure(params string[] statuses) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? Auth)> Requests { get; } = [];
        private int _reads;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.ToString()));
            if (request.RequestUri!.Host == "login.example.test")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"arm-token\",\"token_type\":\"Bearer\"}") };
            var status = statuses[Math.Min(_reads++, statuses.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$$"""{"value":[{"name":"ConnectionStrings__Database","properties":{"reference":"@Microsoft.KeyVault(SecretUri=https://kv.vault.azure.net/secrets/x)","status":"{{{status}}}"}}]}"""),
            };
        }
    }

    private static AzureConfigurationChecker Checker(FakeAzure azure) => new(new HttpClient(azure),
        new AzureCredentials(DeploymentSamples.Tenant, DeploymentSamples.Subscription, "client-id", "client-secret-value"), 4, TimeSpan.Zero,
        "https://login.example.test/", "https://management.example.test/");

    private static readonly Dictionary<string, string> Outputs = new() { ["web_app_name"] = "stock-flow-staging-0a1b2c3d" };

    [Fact]
    public async Task ReferencesResolvingAfterAColdStart_AreAccepted_WithoutKeepingTheirValues()
    {
        var azure = new FakeAzure("AccessToKeyVaultDenied", "Resolved");
        var check = await Checker(azure).CheckAsync(DeploymentSamples.Target(), Outputs);
        Assert.True(check.Resolved);
        Assert.Equal([new ConfigurationReference("ConnectionStrings__Database", "Resolved")], check.References);
        var token = azure.Requests[0];
        Assert.Equal(($"https://login.example.test/{DeploymentSamples.Tenant}/oauth2/v2.0/token", HttpMethod.Post), (token.Url, token.Method));
        Assert.Contains("grant_type=client_credentials", token.Body);
        Assert.Contains("scope=https%3A%2F%2Fmanagement.example.test%2F.default", token.Body);
        var read = azure.Requests[1];
        Assert.Equal(($"https://management.example.test/subscriptions/{DeploymentSamples.Subscription}/resourceGroups/rg-stock-flow-staging-frc" +
            "/providers/Microsoft.Web/sites/stock-flow-staging-0a1b2c3d/config/configreferences/appsettings?api-version=2023-12-01", "Bearer arm-token"),
            (read.Url, read.Auth));
        Assert.All(azure.Requests.Skip(1), r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task ReferencesThatStayDenied_AreReported()
    {
        var check = await Checker(new FakeAzure("AccessToKeyVaultDenied")).CheckAsync(DeploymentSamples.Target(), Outputs);
        Assert.False(check.Resolved);
        Assert.Equal("AccessToKeyVaultDenied", check.References.Single().Status);
    }

    [Fact]
    public async Task UnexpectedSiteNames_AreRefused() =>
        await Assert.ThrowsAsync<DeploymentException>(() => Checker(new FakeAzure("Resolved"))
            .CheckAsync(DeploymentSamples.Target(), new Dictionary<string, string> { ["web_app_name"] = "../../other" }));
}
