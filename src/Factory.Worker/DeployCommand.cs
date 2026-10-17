using System.Text.Json;
using Factory.Core.Deployment;
using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Core.Templates;
using Factory.Infrastructure.Deployment;
using Factory.Infrastructure.Execution;
using Factory.Infrastructure.Templates;

namespace Factory.Worker;

// M13A.1 operator commands (scripts/Deploy-FactoryStaging.ps1). The approval is a separate human step
// between them: `plan` stores the plan and prints its binding hash; `apply` runs only with that exact
// hash. Durable deployment jobs with approval in the Product Workspace follow in M13A.2.
//
//   deploy plan  --target <target.json> --evidence <evidence.json> --evidence-sha256 <sha> --out <directory>
//   deploy apply --plan <directory> --approve <binding sha256> --approved-by <name> [--allow-destructive]
internal static class DeployCommand
{
    private const string ModuleId = DeploymentDefaults.ModuleId;
    private const string ModuleVersion = DeploymentDefaults.ModuleVersion;
    private const string TerraformImage = DeploymentDefaults.TerraformImage;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record SavedPlan(DeploymentTarget Target, DeploymentModule Module, DeploymentBinding Binding, string BindingSha256,
        PlanSummary Summary, string ReleaseApplication);

    private sealed record Evidence(string BindingSha256, DeploymentApproval Approval, DeploymentResult? Result, HealthResult? Health, string? Failure,
        DateTimeOffset CompletedAt);

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration)
    {
        var options = Options(args[1..]);
        try
        {
            return args[0] switch
            {
                "plan" => await Plan(options, configuration),
                "apply" => await Apply(options, configuration),
                _ => Usage(),
            };
        }
        catch (DeploymentException e)
        {
            Console.Error.WriteLine($"{e.Message}{(e.Detail is null ? "" : ": " + e.Detail)}");
            return 2;
        }
    }

    private static async Task<int> Plan(Dictionary<string, string> options, IConfiguration configuration)
    {
        var target = Read<DeploymentTarget>(Required(options, "target")).Validate();
        var evidenceBytes = await File.ReadAllBytesAsync(Required(options, "evidence"));
        if (DeploymentRules.Sha256(evidenceBytes) != Required(options, "evidence-sha256"))
            throw new DeploymentException(DeploymentError.InvalidRelease, "evidence_hash_mismatch");
        var evidence = JsonSerializer.Deserialize<DevelopmentEvidence>(evidenceBytes, DevelopmentEvidenceFormat.Options)
            ?? throw new DeploymentException(DeploymentError.InvalidRelease);
        var name = ApplicationName.Create(evidence.ApplicationName);
        if (name.Kebab != target.Application) throw new DeploymentException(DeploymentError.InvalidTarget, "application_mismatch");

        // The base files come from the hash-verified template the evidence names.
        var templates = new FileSystemTemplateCatalog(Setting(configuration, "Templates:Root"));
        var (template, files) = templates.Load(evidence.TemplateId, evidence.TemplateVersion);
        if (template.ContentSha256 != evidence.TemplateSha256) throw new DeploymentException(DeploymentError.InvalidRelease, "template_mismatch");
        var baseFiles = StarterGenerator.Generate(template, files, name).Files;

        var runner = new DockerSandboxOptions(Setting(configuration, "Verification:RunnerImage"), SandboxLimits.Default);
        Console.WriteLine($"Building the release of job {evidence.JobId} (commit {evidence.FinalCommit}) in the sandbox...");
        var release = await new SandboxReleaseBuilder(runner).BuildAsync(evidence.JobId, evidence, baseFiles);
        Console.WriteLine($"Release {release.Sha256} ({release.Zip.Length} bytes). Planning...");

        var (engine, module) = Engine(configuration);
        var plan = await engine.PlanAsync(target, module, release);
        var directory = Directory.CreateDirectory(Path.Combine(Required(options, "out"), plan.BindingSha256[..16])).FullName;
        await File.WriteAllBytesAsync(Path.Combine(directory, "plan.tfplan"), plan.PlanFile);
        await File.WriteAllBytesAsync(Path.Combine(directory, "release.zip"), release.Zip);
        await File.WriteAllTextAsync(Path.Combine(directory, "plan.log"), plan.Log);
        await File.WriteAllTextAsync(Path.Combine(directory, "plan.json"),
            JsonSerializer.Serialize(new SavedPlan(target, module, plan.Binding, plan.BindingSha256, plan.Summary, release.ApplicationName), Json));

        Console.WriteLine($"Plan for {target.Id}: {plan.Summary.Add} to add, {plan.Summary.Change} to change, {plan.Summary.Destroy} to destroy.");
        foreach (var change in plan.Summary.Changes) Console.WriteLine($"  {string.Join('+', change.Actions),-14} {change.Address}");
        Console.WriteLine($"Saved in {directory}");
        Console.WriteLine($"Binding SHA-256 (approve exactly this): {plan.BindingSha256}");
        return 0;
    }

    private static async Task<int> Apply(Dictionary<string, string> options, IConfiguration configuration)
    {
        var directory = Required(options, "plan");
        var saved = Read<SavedPlan>(Path.Combine(directory, "plan.json"));
        var zip = await File.ReadAllBytesAsync(Path.Combine(directory, "release.zip"));
        var release = new ReleasePackage(saved.Binding.DevelopmentJobId, saved.ReleaseApplication, saved.Binding.SourceCommit, ReleasePackage.Hash(zip), zip);
        var plan = new DeploymentPlan(saved.Binding, saved.BindingSha256, await File.ReadAllBytesAsync(Path.Combine(directory, "plan.tfplan")), saved.Summary, "");
        var approval = new DeploymentApproval(Required(options, "approve"), Required(options, "approved-by"), DateTimeOffset.UtcNow,
            options.ContainsKey("allow-destructive"));
        var (engine, module) = Engine(configuration);
        if (module != saved.Module) throw new DeploymentException(DeploymentError.ModuleHashMismatch);

        DeploymentResult? result = null;
        HealthResult? health = null;
        string? failure = null;
        try
        {
            result = await engine.ApplyAsync(saved.Target, module, release, plan, approval);
            Console.WriteLine($"Applied. Checking {result.Url}/health ...");
            using var http = new HttpClient();
            health = await new HttpHealthChecker(http).CheckAsync(result.Url);
            Console.WriteLine(health.Healthy ? $"Healthy after {health.Attempts} attempt(s)." : $"Not healthy (last status {health.StatusCode?.ToString() ?? "none"}).");
            return health.Healthy ? 0 : 3;
        }
        catch (DeploymentException e)
        {
            failure = e.Message + (e.Detail is null ? "" : ": " + e.Detail);
            throw;
        }
        finally
        {
            // Rejected approvals leave no evidence; everything that reached Terraform does.
            if (result is not null || failure is not null && approval.BindingSha256 == saved.BindingSha256)
                await File.WriteAllTextAsync(Path.Combine(directory, $"apply-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json"),
                    JsonSerializer.Serialize(new Evidence(saved.BindingSha256, approval, result, health, failure, DateTimeOffset.UtcNow), Json));
        }
    }

    private static (TerraformDeploymentEngine Engine, DeploymentModule Module) Engine(IConfiguration configuration)
    {
        var catalog = new FileSystemModuleCatalog(Setting(configuration, "Deployment:ModulesRoot"));
        var credentials = new AzureCredentials(Setting(configuration, "Azure:TenantId"), Setting(configuration, "Azure:SubscriptionId"),
            Setting(configuration, "Azure:ClientId"), Setting(configuration, "Azure:ClientSecret"));
        var engine = new TerraformDeploymentEngine(new TerraformOptions(TerraformImage, Setting(configuration, "Deployment:PluginCache"), credentials), catalog);
        return (engine, catalog.Load(ModuleId, ModuleVersion).Module);
    }

    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new DeploymentException(DeploymentError.InvalidTarget, "unreadable_" + Path.GetFileName(path));

    private static string Setting(IConfiguration configuration, string key) => configuration[key] is { Length: > 0 } value ? value
        : throw new DeploymentException(DeploymentError.EngineUnavailable, key + "_not_configured");

    private static string Required(Dictionary<string, string> options, string name) => options.TryGetValue(name, out var value) && value.Length > 0 ? value
        : throw new DeploymentException(DeploymentError.InvalidTarget, "missing --" + name);

    private static Dictionary<string, string> Options(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var name = args[i][2..];
            options[name] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }
        return options;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: deploy plan --target <file> --evidence <file> --evidence-sha256 <sha> --out <dir> | " +
            "deploy apply --plan <dir> --approve <binding sha256> --approved-by <name> [--allow-destructive]");
        return 64;
    }
}
