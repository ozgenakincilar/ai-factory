using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Core.Development;
using Factory.Core.Templates;

namespace Factory.Core.Deployment;

// M13A: deployment of a verified release to a cloud target. The engine's lifecycle (plan, approve the
// exact plan, apply that plan, verify health) is independent of Terraform; the Terraform CLI, module
// layout and state live behind IDeploymentEngine in Infrastructure.

public enum DeploymentError
{
    InvalidTarget, InvalidModule, ModuleHashMismatch, InvalidRelease, ReleaseCommitMismatch, ReleaseBuildFailed, PackageTooLarge,
    PlanFailed, PlanTooLarge, ApplyFailed, ApprovalMismatch, PlanTampered, DestructivePlan, EngineUnavailable,
}

public sealed class DeploymentException(DeploymentError error, string? detail = null) : Exception("deployment_" + error)
{
    public DeploymentError Error { get; } = error;
    // Factory-controlled diagnostic text (tool output is bounded and never holds credentials).
    public string? Detail { get; } = detail;
}

// A versioned infrastructure module; the content hash pins its exact files (as for templates).
public sealed record DeploymentModule(string Id, string Version, string Directory, string ContentSha256);

// One BYOC target as created by the operator bootstrap. Non-secret; the deployment identity's
// credentials are configured separately and never enter plans, evidence or logs.
public sealed partial record DeploymentTarget(string Cloud, string Environment, string Application, string SubscriptionId, string TenantId,
    string Location, string ResourceGroup, string NameSuffix, string StateResourceGroup, string StateAccount, string StateContainer, string StateKey)
{
    public string Id => $"{Cloud}/{SubscriptionId}/{ResourceGroup}/{Environment}";

    public DeploymentTarget Validate()
    {
        if (Cloud != "azure" || Environment != "staging" || !Kebab().IsMatch(Application) || !Guid.TryParse(SubscriptionId, out _) ||
            !Guid.TryParse(TenantId, out _) || !Name().IsMatch(Location) || !Name().IsMatch(ResourceGroup) || !Hex8().IsMatch(NameSuffix) ||
            !Name().IsMatch(StateResourceGroup) || !StorageAccount().IsMatch(StateAccount) || !Name().IsMatch(StateContainer) ||
            !StateKeyPattern().IsMatch(StateKey))
            throw new DeploymentException(DeploymentError.InvalidTarget);
        return this;
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{1,30}[a-z0-9]\z")] private static partial Regex Kebab();
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{1,62}\z")] private static partial Regex Name();
    [GeneratedRegex(@"\A[0-9a-f]{8}\z")] private static partial Regex Hex8();
    [GeneratedRegex(@"\A[a-z0-9]{3,24}\z")] private static partial Regex StorageAccount();
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9.-]{0,62}\.tfstate\z")] private static partial Regex StateKeyPattern();
}

// The deployable artifact of a verified development job: the published API as a zip. Sha256 covers
// the exact bytes; SourceCommit is the evidence's final commit the package was built from.
public sealed record ReleasePackage(Guid DevelopmentJobId, string ApplicationName, string SourceCommit, string Sha256, byte[] Zip)
{
    public const int MaxBytes = 200 * 1024 * 1024;

    public static string Hash(byte[] zip) => Convert.ToHexStringLower(SHA256.HashData(zip));

    public ReleasePackage Validate()
    {
        if (Zip.Length is 0 or > MaxBytes || Hash(Zip) != Sha256 || SourceCommit is not { Length: 40 })
            throw new DeploymentException(DeploymentError.InvalidRelease);
        return this;
    }
}

public sealed record PlannedChange(string Address, string Type, IReadOnlyList<string> Actions);

public sealed record PlanSummary(int Add, int Change, int Destroy, IReadOnlyList<PlannedChange> Changes)
{
    // Deletes and replacements (delete+create) count as destructive.
    public bool Destructive => Destroy > 0;

    // Parses `terraform show -json` of a saved plan; no-op and read actions are not changes.
    public static PlanSummary FromTerraformJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var changes = new List<PlannedChange>();
            int add = 0, change = 0, destroy = 0;
            if (document.RootElement.TryGetProperty("resource_changes", out var resources))
                foreach (var resource in resources.EnumerateArray())
                {
                    var actions = resource.GetProperty("change").GetProperty("actions").EnumerateArray().Select(a => a.GetString() ?? "").ToList();
                    if (actions.All(a => a is "no-op" or "read")) continue;
                    if (actions.Contains("create")) add++;
                    if (actions.Contains("update")) change++;
                    if (actions.Contains("delete")) destroy++;
                    changes.Add(new(resource.GetProperty("address").GetString() ?? "", resource.GetProperty("type").GetString() ?? "", actions));
                }
            return new(add, change, destroy, changes);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new DeploymentException(DeploymentError.PlanFailed, "plan_json_invalid");
        }
    }
}

// Everything a human approves, bound into one hash: the exact plan bytes, the target, the module
// version and content, the release bytes and source commit, and the module inputs. Any change
// produces a different binding, so an approval never carries over to another plan.
public sealed record DeploymentBinding(int FormatVersion, string TargetId, string ModuleId, string ModuleVersion, string ModuleSha256,
    Guid DevelopmentJobId, string ReleaseSha256, string SourceCommit, string InputsSha256, string PlanSha256)
{
    public const int CurrentFormat = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, Json)));
}

public sealed record DeploymentPlan(DeploymentBinding Binding, string BindingSha256, byte[] PlanFile, PlanSummary Summary, string Log);

// A human's approval of one binding. Recorded by the caller (CLI now, orchestration in M13A.2).
public sealed record DeploymentApproval(string BindingSha256, string ApprovedBy, DateTimeOffset ApprovedAt, bool AllowDestructive = false);

public sealed record DeploymentResult(string BindingSha256, string Url, IReadOnlyDictionary<string, string> Outputs, string Log);

public sealed record HealthResult(string Url, bool Healthy, int? StatusCode, int Attempts, TimeSpan Duration);

public interface IDeploymentEngine
{
    Task<DeploymentPlan> PlanAsync(DeploymentTarget target, DeploymentModule module, ReleasePackage release, CancellationToken ct = default);

    // Applies exactly the approved plan; the engine re-checks the binding first (DeploymentRules.Verify).
    Task<DeploymentResult> ApplyAsync(DeploymentTarget target, DeploymentModule module, ReleasePackage release, DeploymentPlan plan,
        DeploymentApproval approval, CancellationToken ct = default);
}

public interface IDeploymentModuleCatalog
{
    // Verifies the registered content hash; a changed module is refused.
    (DeploymentModule Module, IReadOnlyList<TemplateFile> Files) Load(string id, string version);
}

public interface IReleaseBuilder
{
    // Re-creates the evidence's commits from the regenerated base files and the evidence diff, checks
    // both commit ids, and publishes the API in the sandbox.
    Task<ReleasePackage> BuildAsync(Guid developmentJobId, DevelopmentEvidence evidence, IReadOnlyList<TemplateFile> baseFiles, CancellationToken ct = default);
}

public interface IHealthChecker
{
    // path: the template's readiness endpoint (1.1.0+: /health/ready, which needs the database), else /health.
    Task<HealthResult> CheckAsync(string baseUrl, string path = "/health", CancellationToken ct = default);
}

public static class DeploymentRules
{
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string InputsHash(IReadOnlyDictionary<string, string> inputs)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in inputs.OrderBy(p => p.Key, StringComparer.Ordinal)) builder.Append(key).Append('\0').Append(value).Append('\0');
        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    // The module inputs follow from the target and release only; nothing comes from a model or a user.
    public static IReadOnlyDictionary<string, string> Inputs(DeploymentTarget target, ReleasePackage release) => new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["application_name"] = target.Application,
        ["environment"] = target.Environment,
        ["location"] = target.Location,
        ["name_suffix"] = target.NameSuffix,
        ["release_package"] = ReleasePath(release),
        ["release_sha256"] = release.Sha256,
        ["resource_group_name"] = target.ResourceGroup,
    };

    // The file name carries the hash, so a new release always changes the plan (and redeploys).
    public static string ReleasePath(ReleasePackage release) => "release/" + release.Sha256 + ".zip";

    public static DeploymentBinding Bind(DeploymentTarget target, DeploymentModule module, ReleasePackage release, byte[] planFile) =>
        new(DeploymentBinding.CurrentFormat, target.Id, module.Id, module.Version, module.ContentSha256, release.DevelopmentJobId,
            release.Sha256, release.SourceCommit, InputsHash(Inputs(target, release)), Sha256(planFile));

    // Before apply: the plan bytes, target, module, release and inputs must still produce the approved
    // binding, and a destructive plan needs an explicit destructive approval.
    public static void Verify(DeploymentTarget target, DeploymentModule module, ReleasePackage release, DeploymentPlan plan, DeploymentApproval approval)
    {
        var binding = Bind(target, module, release, plan.PlanFile);
        if (binding != plan.Binding || binding.Hash() != plan.BindingSha256) throw new DeploymentException(DeploymentError.PlanTampered);
        if (approval.BindingSha256 != plan.BindingSha256 || string.IsNullOrWhiteSpace(approval.ApprovedBy))
            throw new DeploymentException(DeploymentError.ApprovalMismatch);
        if (plan.Summary.Destructive && !approval.AllowDestructive) throw new DeploymentException(DeploymentError.DestructivePlan);
    }
}
