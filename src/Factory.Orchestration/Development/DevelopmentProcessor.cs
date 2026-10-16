using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core.Architecture;
using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Core.Reviews;
using Factory.Core.Templates;

namespace Factory.Orchestration.Development;

public enum DevelopmentOutcome { Idle, Completed, Failed, LeaseLost }

public sealed record DevelopmentModel(string Name, IModelProvider Provider, long InputMicrosPerMillion, long OutputMicrosPerMillion);

// Claims one development job, re-checks the approved artifact bytes, generates the starter
// application, runs the Developer Agent under the job cap while renewing the lease, and stores the
// diff, requirement links, usage and verification as evidence. A lost lease cancels the run; the
// database then ends the job as worker_lost instead of paying for it again.
public sealed class DevelopmentProcessor(IDevelopmentQueue queue, ITemplateCatalog catalog, DevelopmentModel model, IWorkspaceRunner runner,
    ISandbox verifier, IWorkspaceDiffer differ, DevelopmentLimits limits, long totalCapMicros)
{
    public async Task<DevelopmentOutcome> ProcessNextAsync(string workerId, CancellationToken ct)
    {
        var token = RandomNumberGenerator.GetBytes(32);
        var job = await queue.ClaimDevelopmentAsync(workerId, token, limits.CostCapMicros, totalCapMicros, ct);
        if (job is null) return DevelopmentOutcome.Idle;

        ReviewEnvelope? envelope = null;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(job.Artifact ?? "");
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) == job.ArtifactSha256)
                envelope = JsonSerializer.Deserialize<ReviewEnvelope>(bytes, ArchitectureJson.Options);
        }
        catch (JsonException) { }
        if (envelope?.Analysis is null || envelope.Architecture is null || envelope.CaseId != job.CaseId || envelope.Version != job.ArtifactVersion)
        {
            await queue.FailDevelopmentAsync(job.JobId, token, "invalid_artifact", ct);
            return DevelopmentOutcome.Failed;
        }
        TemplateDescriptor template;
        GeneratedApplication application;
        try
        {
            var loaded = catalog.Load(job.TemplateId, job.TemplateVersion);
            template = loaded.Template;
            application = StarterGenerator.Generate(template, loaded.Files, ApplicationName.Create(job.ApplicationName));
        }
        catch (TemplateException ex)
        {
            await queue.FailDevelopmentAsync(job.JobId, token,
                ex.Error == TemplateError.InvalidApplicationName ? "invalid_application_name" : "template_unavailable", ct);
            return DevelopmentOutcome.Failed;
        }

        using var lease = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Heartbeat(job.JobId, token, lease);
        var workspace = new DevelopmentWorkspace(application);
        DevelopmentResult result;
        WorkspaceDiff? diff = null;
        try
        {
            var agent = new DeveloperAgent(model.Provider, runner, verifier,
                new CostGuard(model.InputMicrosPerMillion, model.OutputMicrosPerMillion, limits.CostCapMicros), limits);
            result = await agent.RunAsync(new DevelopmentTask(envelope.Request.Task, envelope.Analysis, envelope.Architecture), workspace, lease.Token);
            if (result.Changes.Count > 0)
            {
                try { diff = await differ.CreateAsync(workspace.BaseFiles, result.Files, lease.Token); }
                catch (SandboxUnavailableException) when (result.Status != DevelopmentStatus.Verified) { }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return DevelopmentOutcome.LeaseLost; }
        finally
        {
            await lease.CancelAsync();
            await heartbeat;
        }
        // A verified job without a diff (no changes) is reported as not finished: nothing was implemented.
        var status = result.Status == DevelopmentStatus.Verified && diff is null ? DevelopmentStatus.NotFinished : result.Status;
        await queue.CompleteDevelopmentAsync(job.JobId, token, new DevelopmentEvidence(1, job.JobId, job.ProjectId, job.CaseId, job.ArtifactVersion,
            job.ArtifactSha256, template.Id, template.Version, template.ContentSha256, application.Name.Value, model.Name,
            DevelopmentEvidenceFormat.Status(status), status == result.Status ? result.FailureCode : "no_changes", result.Turns, result.Actions,
            result.CostMicros,
            result.Calls.Select(c => new EvidenceCall(c.Turn, c.Status.ToString().ToLowerInvariant(), c.Usage?.Model, c.Usage?.InputTokens,
                c.Usage?.OutputTokens, c.ReservedMicros, c.CostMicros)).ToList(),
            result.Changes, diff?.BaseCommit, diff?.FinalCommit, diff?.Diff, diff?.DiffSha256, result.Verification, result.Log,
            result.Verdict?.Checks, result.Corrections), ct);
        return DevelopmentOutcome.Completed;
    }

    private async Task Heartbeat(Guid jobId, byte[] token, CancellationTokenSource lease)
    {
        while (!lease.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(DevelopmentJobLimits.RenewSeconds), lease.Token); }
            catch (OperationCanceledException) { return; }
            try { await queue.RenewDevelopmentAsync(jobId, token, lease.Token); }
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
