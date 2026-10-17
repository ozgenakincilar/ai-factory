using System.Security.Cryptography;
using System.Text.Json;
using Factory.Core.Deployment;
using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Core.Orchestration;
using Factory.Core.Templates;

namespace Factory.Orchestration.Deployment;

public enum DeploymentRunOutcome { Idle, Planned, Applied, Failed, LeaseLost }

// M13A.2: one deployment phase per claim. Plan: re-check the verified evidence, build the release in
// the sandbox, plan with the current module and store the plan with its binding. Apply (after a human
// approved that binding): rebuild the release from the same evidence, require the planned release
// hash, apply exactly the stored plan, then verify health and the secret references. A lost lease
// cancels the work; the database ends the deployment as worker_lost and nothing is retried.
public sealed class DeploymentProcessor(IDeploymentQueue queue, ITemplateCatalog templates, IDeploymentModuleCatalog modules, IReleaseBuilder builder,
    IDeploymentEngine engine, IHealthChecker health, IConfigurationChecker configuration, string moduleId, string moduleVersion)
{
    public async Task<DeploymentRunOutcome> ProcessNextAsync(string workerId, CancellationToken ct)
    {
        var token = RandomNumberGenerator.GetBytes(32);
        var claim = await queue.ClaimDeploymentAsync(workerId, token, ct);
        if (claim is null) return DeploymentRunOutcome.Idle;

        using var lease = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Heartbeat(claim.DeploymentId, token, lease);
        try
        {
            return claim.Phase == DeploymentPhase.Plan ? await Plan(claim, token, lease.Token) : await Apply(claim, token, lease.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return DeploymentRunOutcome.LeaseLost; }
        finally
        {
            await lease.CancelAsync();
            await heartbeat;
        }
    }

    private async Task<DeploymentRunOutcome> Plan(ClaimedDeployment claim, byte[] token, CancellationToken ct)
    {
        try
        {
            var (evidence, baseFiles) = Source(claim);
            var release = await builder.BuildAsync(claim.DevelopmentJobId, evidence, baseFiles, ct);
            if (release.ApplicationName != claim.Target.Application) throw new DeploymentException(DeploymentError.InvalidTarget, "application_mismatch");
            var module = modules.Load(moduleId, moduleVersion).Module;
            var plan = await engine.PlanAsync(claim.Target, module, release, ct);
            await queue.RecordPlanAsync(claim.DeploymentId, token, module, release, plan, CancellationToken.None);
            return DeploymentRunOutcome.Planned;
        }
        catch (DeploymentException e)
        {
            await queue.FailDeploymentAsync(claim.DeploymentId, token, Code(e), Outcome(claim, e), CancellationToken.None);
            return DeploymentRunOutcome.Failed;
        }
    }

    private async Task<DeploymentRunOutcome> Apply(ClaimedDeployment claim, byte[] token, CancellationToken ct)
    {
        try
        {
            if (claim.Binding is null || claim.BindingSha256 is null || claim.PlanFile is null || claim.Summary is null || claim.ModuleId is null ||
                claim.ModuleVersion is null || claim.ApprovedBy is null || claim.ApprovedAt is null || claim.AllowDestructive is null)
                throw new DeploymentException(DeploymentError.ApprovalMismatch, "approval_missing");
            var (evidence, baseFiles) = Source(claim);
            // The release is not stored; the deterministic rebuild must reproduce the planned bytes.
            var release = await builder.BuildAsync(claim.DevelopmentJobId, evidence, baseFiles, ct);
            if (release.Sha256 != claim.Binding.ReleaseSha256) throw new DeploymentException(DeploymentError.PlanTampered, "release_mismatch");
            var module = modules.Load(claim.ModuleId, claim.ModuleVersion).Module;
            var plan = new DeploymentPlan(claim.Binding, claim.BindingSha256, claim.PlanFile, claim.Summary, "");
            var approval = new DeploymentApproval(claim.BindingSha256, claim.ApprovedBy, claim.ApprovedAt.Value, claim.AllowDestructive.Value);
            var result = await engine.ApplyAsync(claim.Target, module, release, plan, approval, ct);
            var checkedHealth = await health.CheckAsync(result.Url, ReadinessPath(evidence), ct);
            var checkedConfiguration = await configuration.CheckAsync(claim.Target, result.Outputs, ct);
            var failure = !checkedHealth.Healthy ? "health_check_failed" : !checkedConfiguration.Resolved ? "configuration_unresolved" : null;
            await queue.CompleteDeploymentAsync(claim.DeploymentId, token, failure is null, result.Url,
                new DeploymentOutcome(claim.BindingSha256, result.Url, result.Outputs, checkedHealth, checkedConfiguration, failure, claim.ApprovedBy,
                    claim.ApprovedAt), failure, CancellationToken.None);
            return DeploymentRunOutcome.Applied;
        }
        catch (DeploymentException e)
        {
            await queue.FailDeploymentAsync(claim.DeploymentId, token, Code(e), Outcome(claim, e), CancellationToken.None);
            return DeploymentRunOutcome.Failed;
        }
    }

    // The evidence must be this verified job's; the base files come from the hash-verified template.
    private string ReadinessPath(DevelopmentEvidence evidence) => templates.Load(evidence.TemplateId, evidence.TemplateVersion).Template.ReadinessPath ?? "/health";

    private (DevelopmentEvidence Evidence, IReadOnlyList<TemplateFile> BaseFiles) Source(ClaimedDeployment claim)
    {
        DevelopmentEvidence? evidence;
        try { evidence = JsonSerializer.Deserialize<DevelopmentEvidence>(claim.Evidence, DevelopmentEvidenceFormat.Options); }
        catch (JsonException) { evidence = null; }
        if (evidence is null || evidence.JobId != claim.DevelopmentJobId || evidence.ProjectId != claim.ProjectId || evidence.Status != DevelopmentJobStatus.Verified)
            throw new DeploymentException(DeploymentError.InvalidRelease, "evidence_invalid");
        try
        {
            var (template, files) = templates.Load(evidence.TemplateId, evidence.TemplateVersion);
            if (template.ContentSha256 != evidence.TemplateSha256) throw new DeploymentException(DeploymentError.InvalidRelease, "template_mismatch");
            return (evidence, StarterGenerator.Generate(template, files, ApplicationName.Create(evidence.ApplicationName)).Files);
        }
        catch (TemplateException) { throw new DeploymentException(DeploymentError.InvalidRelease, "template_unavailable"); }
    }

    internal static string Code(DeploymentException e) => e.Detail switch
    {
        "credentials_target_mismatch" => "target_not_configured",
        "application_mismatch" => "application_mismatch",
        "release_mismatch" => "release_mismatch",
        "evidence_invalid" or "template_mismatch" or "template_unavailable" => e.Detail,
        _ => System.Text.RegularExpressions.Regex.Replace(e.Error.ToString(), "(?<=[a-z])(?=[A-Z])", "_").ToLowerInvariant(),
    };

    // Engine details are Factory-controlled text with bounded, redacted tool output; they help the
    // reviewer see why a plan or apply failed.
    private static DeploymentOutcome Outcome(ClaimedDeployment claim, DeploymentException e)
    {
        var text = e.Detail is null ? e.Message : e.Message + ": " + e.Detail;
        return new(claim.BindingSha256 ?? "", null, null, null, null, text.Length <= 4000 ? text : text[..4000], claim.ApprovedBy, claim.ApprovedAt);
    }

    private async Task Heartbeat(Guid deploymentId, byte[] token, CancellationTokenSource lease)
    {
        while (!lease.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(DeploymentJobLimits.RenewSeconds), lease.Token); }
            catch (OperationCanceledException) { return; }
            try { await queue.RenewDeploymentAsync(deploymentId, token, lease.Token); }
            catch (OrchestrationException ex) when (ex.Error is OrchestrationError.LeaseLost or OrchestrationError.Forbidden or OrchestrationError.NotFound)
            {
                await lease.CancelAsync();
                return;
            }
            catch (OrchestrationException) { }
            catch (OperationCanceledException) { return; }
        }
    }
}
