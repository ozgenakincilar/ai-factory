using System.Security.Cryptography;
using Factory.Core.Execution;
using Factory.Core.Orchestration;
using Factory.Core.Templates;

namespace Factory.Orchestration.Verification;

public enum VerificationOutcome { Idle, Completed, Failed, LeaseLost }

// Claims one verification job, generates the application from the hash-verified template, runs the
// Factory's verification steps in the sandbox and stores the report as evidence. The lease is
// renewed while the sandbox runs; losing it stops the sandbox and leaves the job to a later claim.
public sealed class VerificationProcessor(IVerificationQueue queue, ITemplateCatalog catalog, ISandbox sandbox)
{
    public const string StarterTemplateId = "dotnet-angular-postgres-ddd";

    public async Task<VerificationOutcome> ProcessNextAsync(string workerId, CancellationToken ct)
    {
        var token = RandomNumberGenerator.GetBytes(32);
        var job = await queue.ClaimVerificationAsync(workerId, token, ct);
        if (job is null) return VerificationOutcome.Idle;

        TemplateDescriptor template;
        GeneratedApplication application;
        try
        {
            // Only the starter template has a verification plan so far.
            if (job.TemplateId != StarterTemplateId) throw new TemplateException(TemplateError.TemplateNotFound);
            var loaded = catalog.Load(job.TemplateId, job.TemplateVersion);
            template = loaded.Template;
            application = StarterGenerator.Generate(template, loaded.Files, ApplicationName.Create(job.ApplicationName));
        }
        catch (TemplateException ex)
        {
            await queue.FailVerificationAsync(job.JobId, token,
                ex.Error == TemplateError.InvalidApplicationName ? "invalid_application_name" : "template_unavailable", ct);
            return VerificationOutcome.Failed;
        }

        using var lease = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Heartbeat(job.JobId, token, lease);
        SandboxReport report;
        try { report = await sandbox.RunAsync(application.Files, StarterVerification.Steps(application.Name), lease.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return VerificationOutcome.LeaseLost; }
        finally
        {
            await lease.CancelAsync();
            await heartbeat;
        }
        await queue.CompleteVerificationAsync(job.JobId, token, new VerificationEvidence(1, job.JobId, job.ProjectId, template.Id, template.Version,
            template.ContentSha256, application.Name.Value, application.ManifestSha256, report), ct);
        return VerificationOutcome.Completed;
    }

    // Renews until the work ends; a lost lease cancels the sandbox. Transient renewal failures are
    // retried at the next interval while the lease (90 s) still covers them.
    private async Task Heartbeat(Guid jobId, byte[] token, CancellationTokenSource lease)
    {
        while (!lease.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(VerificationLimits.RenewSeconds), lease.Token); }
            catch (OperationCanceledException) { return; }
            try { await queue.RenewVerificationAsync(jobId, token, lease.Token); }
            catch (OrchestrationException ex) when (ex.Error is OrchestrationError.LeaseLost or OrchestrationError.Forbidden or OrchestrationError.NotFound)
            {
                await lease.CancelAsync();
                return;
            }
            catch (OrchestrationException) { }
        }
    }
}
