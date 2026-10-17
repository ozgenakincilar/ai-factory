using System.Text;
using System.Text.Json;
using Factory.Core.Deployment;
using Factory.Core.Execution;
using Factory.Infrastructure.Execution;

namespace Factory.Infrastructure.Deployment;

// The deployment identity (service principal). ToString never prints the secret.
public sealed record AzureCredentials(string TenantId, string SubscriptionId, string ClientId, string ClientSecret)
{
    public override string ToString() => $"AzureCredentials {{ TenantId = {TenantId}, SubscriptionId = {SubscriptionId}, ClientId = {ClientId} }}";
}

// The current pinned engine inputs: the Terraform image and the module version new plans use.
public static class DeploymentDefaults
{
    public const string TerraformImage = "hashicorp/terraform@sha256:c7926feace05d0f7e73542842bf3945924e955a1f782cf000ccbb8d18fa42d77";
    public const string ModuleId = "azure-app-service-postgres";
    public const string ModuleVersion = "1.1.0";
}

// Image must be pinned by digest. PluginCache keeps downloaded providers between runs; the module's
// lock file pins their hashes, so a changed cache entry is refused by Terraform.
public sealed record TerraformOptions(string Image, string PluginCache, AzureCredentials Credentials, string DockerPath = "docker")
{
    public static readonly TimeSpan PlanTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ApplyTimeout = TimeSpan.FromMinutes(60);
}

// M13A: Terraform behind IDeploymentEngine. Every command runs in a fresh, short-lived container of
// the pinned Terraform image: non-root, read-only root filesystem, no capabilities, CPU/memory/PID
// limits, only the per-operation working directory and the provider cache mounted. It needs the
// network (Azure and the provider registry). The credentials reach Terraform as environment variables
// forwarded by name, never on a command line, in files, plans or evidence. Apply runs exactly the
// approved saved plan; Terraform itself refuses a saved plan whose state has changed since.
public sealed class TerraformDeploymentEngine(TerraformOptions options, IDeploymentModuleCatalog catalog) : IDeploymentEngine
{
    private const string PlanName = "factory.tfplan";
    private readonly DockerSandboxOptions _docker = new DockerSandboxOptions(options.Image,
        SandboxLimits.Default with { OutputBytes = 4 * 1024 * 1024 }, options.DockerPath).Validate();

    public async Task<DeploymentPlan> PlanAsync(DeploymentTarget target, DeploymentModule module, ReleasePackage release, CancellationToken ct = default)
    {
        CheckTarget(target);
        release.Validate();
        var log = new StringBuilder();
        var work = Prepare(target, module, release);
        try
        {
            await Init(work, target, log, ct);
            await Run(work, ["plan", "-input=false", "-no-color", "-lock-timeout=120s", "-out=" + PlanName], TerraformOptions.PlanTimeout, log,
                DeploymentError.PlanFailed, ct);
            var planFile = await File.ReadAllBytesAsync(Path.Combine(work.FullName, PlanName), ct);
            var summary = await Show(work, ct);
            var binding = DeploymentRules.Bind(target, module, release, planFile);
            return new(binding, binding.Hash(), planFile, summary, log.ToString());
        }
        finally { Delete(work); }
    }

    public async Task<DeploymentResult> ApplyAsync(DeploymentTarget target, DeploymentModule module, ReleasePackage release, DeploymentPlan plan,
        DeploymentApproval approval, CancellationToken ct = default)
    {
        CheckTarget(target);
        release.Validate();
        DeploymentRules.Verify(target, module, release, plan, approval);
        var log = new StringBuilder();
        var work = Prepare(target, module, release);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(work.FullName, PlanName), plan.PlanFile, ct);
            await Init(work, target, log, ct);
            // The summary that was approved must be the one Terraform reads from these exact bytes.
            var summary = await Show(work, ct);
            if (JsonSerializer.Serialize(summary) != JsonSerializer.Serialize(plan.Summary)) throw new DeploymentException(DeploymentError.PlanTampered);
            await Run(work, ["apply", "-input=false", "-no-color", "-lock-timeout=120s", PlanName], TerraformOptions.ApplyTimeout, log,
                DeploymentError.ApplyFailed, ct);
            var outputs = Outputs(await Run(work, ["output", "-json", "-no-color"], TerraformOptions.PlanTimeout, null, DeploymentError.ApplyFailed, ct));
            var url = outputs.GetValueOrDefault("url") ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new DeploymentException(DeploymentError.ApplyFailed, "url_output_invalid");
            return new(plan.BindingSha256, url, outputs, log.ToString());
        }
        finally { Delete(work); }
    }

    // Wrong-target denial: the identity's tenant and subscription must be the target's.
    private void CheckTarget(DeploymentTarget target)
    {
        target.Validate();
        if (!string.Equals(target.TenantId, options.Credentials.TenantId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(target.SubscriptionId, options.Credentials.SubscriptionId, StringComparison.OrdinalIgnoreCase))
            throw new DeploymentException(DeploymentError.InvalidTarget, "credentials_target_mismatch");
    }

    private DirectoryInfo Prepare(DeploymentTarget target, DeploymentModule module, ReleasePackage release)
    {
        var (loaded, files) = catalog.Load(module.Id, module.Version);
        if (loaded != module) throw new DeploymentException(DeploymentError.ModuleHashMismatch);
        var work = Directory.CreateTempSubdirectory("factory-tf-");
        foreach (var file in files)
        {
            var path = Path.Combine(work.FullName, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(file.Content));
        }
        var inputs = DeploymentRules.Inputs(target, release);
        File.WriteAllText(Path.Combine(work.FullName, "terraform.tfvars.json"), JsonSerializer.Serialize(inputs));
        var package = Path.Combine(work.FullName, DeploymentRules.ReleasePath(release).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        File.WriteAllBytes(package, release.Zip);
        return work;
    }

    private Task Init(DirectoryInfo work, DeploymentTarget target, StringBuilder log, CancellationToken ct) => Run(work,
    [
        "init", "-input=false", "-no-color", "-lockfile=readonly",
        "-backend-config=resource_group_name=" + target.StateResourceGroup, "-backend-config=storage_account_name=" + target.StateAccount,
        "-backend-config=container_name=" + target.StateContainer, "-backend-config=key=" + target.StateKey,
    ], TerraformOptions.PlanTimeout, log, DeploymentError.PlanFailed, ct);

    private async Task<PlanSummary> Show(DirectoryInfo work, CancellationToken ct) =>
        PlanSummary.FromTerraformJson(await Run(work, ["show", "-json", "-no-color", PlanName], TerraformOptions.PlanTimeout, null, DeploymentError.PlanFailed, ct));

    private static Dictionary<string, string> Outputs(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var output in document.RootElement.EnumerateObject())
                if (!output.Value.GetProperty("sensitive").GetBoolean() && output.Value.GetProperty("value").ValueKind == JsonValueKind.String)
                    outputs[output.Name] = output.Value.GetProperty("value").GetString()!;
            return outputs;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new DeploymentException(DeploymentError.ApplyFailed, "outputs_invalid");
        }
    }

    internal IReadOnlyList<string> RunArguments(string name, string work, IReadOnlyList<string> terraform)
    {
        var uid = DockerSandboxOptions.RunnerUid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        List<string> arguments =
        [
            "run", "--rm", "--name", name, "--init", "--read-only", "--ipc", "none",
            "--user", uid + ":" + uid, "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "--cpus", "2", "--memory", "2048m", "--memory-swap", "2048m", "--pids-limit", "512",
            "--tmpfs", "/tmp:rw,nosuid,nodev,size=512m,mode=1777",
            "-e", "HOME=/tmp", "-e", "TF_IN_AUTOMATION=1", "-e", "TF_INPUT=0", "-e", "CHECKPOINT_DISABLE=1", "-e", "TF_PLUGIN_CACHE_DIR=/plugins",
            "-e", "ARM_TENANT_ID", "-e", "ARM_SUBSCRIPTION_ID", "-e", "ARM_CLIENT_ID", "-e", "ARM_CLIENT_SECRET",
            "-v", work + ":/w", "-v", Path.GetFullPath(options.PluginCache) + ":/plugins", "-w", "/w",
            "--label", "factory.terraform=1", options.Image,
        ];
        arguments.AddRange(terraform);
        return arguments;
    }

    private async Task<string> Run(DirectoryInfo work, IReadOnlyList<string> terraform, TimeSpan timeout, StringBuilder? log, DeploymentError failure,
        CancellationToken ct)
    {
        Directory.CreateDirectory(options.PluginCache);
        var name = "factory-tf-" + Guid.NewGuid().ToString("N");
        var environment = new Dictionary<string, string>
        {
            ["ARM_TENANT_ID"] = options.Credentials.TenantId, ["ARM_SUBSCRIPTION_ID"] = options.Credentials.SubscriptionId,
            ["ARM_CLIENT_ID"] = options.Credentials.ClientId, ["ARM_CLIENT_SECRET"] = options.Credentials.ClientSecret,
        };
        DockerSandbox.ProcessResult result;
        try
        {
            result = await DockerSandbox.Docker(_docker, RunArguments(name, work.FullName, terraform), null, timeout, ct, environment);
        }
        catch (System.ComponentModel.Win32Exception) { throw new DeploymentException(DeploymentError.EngineUnavailable, "docker_unavailable"); }
        finally
        {
            // Killing the docker client does not stop the container; a timed-out or cancelled command must not keep running.
            try { await DockerSandbox.Docker(_docker, ["rm", "--force", name], null, TimeSpan.FromMinutes(1), CancellationToken.None); }
            catch (System.ComponentModel.Win32Exception) { }
        }
        var output = Redact(result.Output);
        log?.Append("$ terraform ").AppendLine(terraform[0]).AppendLine(output);
        if (result.TimedOut) throw new DeploymentException(failure, terraform[0] + "_timeout");
        if (result.Exit != 0) throw new DeploymentException(failure, terraform[0] + "_failed: " + Tail(output));
        if (result.Truncated) throw new DeploymentException(failure == DeploymentError.PlanFailed ? DeploymentError.PlanTooLarge : failure, terraform[0] + "_output_truncated");
        return output;
    }

    // Defence in depth: the secret is never passed to Terraform as text, but must never be logged either.
    private string Redact(string text) => options.Credentials.ClientSecret.Length == 0 ? text : text.Replace(options.Credentials.ClientSecret, "[redacted]", StringComparison.Ordinal);

    private static string Tail(string text) => text.Length <= 2000 ? text : text[^2000..];

    private static void Delete(DirectoryInfo work)
    {
        try { work.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
