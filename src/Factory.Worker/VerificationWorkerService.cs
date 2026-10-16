using Factory.Orchestration.Verification;

namespace Factory.Worker;

// One verification at a time per worker: each job builds and tests a whole application in a
// sandbox sized by its limits. Leases in the database decide ownership, as for workflow steps.
public sealed class VerificationWorkerService(VerificationProcessor processor, ILogger<VerificationWorkerService> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var id = $"{Environment.MachineName}:{Environment.ProcessId}:verification:{Guid.NewGuid():N}";
        logger.LogInformation("Verification worker {WorkerId} started", id);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var outcome = await processor.ProcessNextAsync(id, stoppingToken);
                if (outcome == VerificationOutcome.Idle) await Task.Delay(Idle, stoppingToken);
                else logger.LogInformation("Verification worker {WorkerId}: job {Outcome}", id, outcome);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Exception type only: messages may contain SQL or sandbox output.
                logger.LogError("Verification worker {WorkerId}: job processing failed; exception type {ExceptionType}", id, ex.GetType().FullName);
                try { await Task.Delay(Backoff, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
