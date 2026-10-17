using Factory.Orchestration.Deployment;

namespace Factory.Worker;

// One deployment phase at a time per worker: plans and applies change cloud infrastructure. Database
// leases decide ownership; a lost worker ends its deployment (worker_lost) and nothing is re-applied.
public sealed class DeploymentWorkerService(DeploymentProcessor processor, ILogger<DeploymentWorkerService> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var id = $"{Environment.MachineName}:{Environment.ProcessId}:deployment:{Guid.NewGuid():N}";
        logger.LogInformation("Deployment worker {WorkerId} started", id);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var outcome = await processor.ProcessNextAsync(id, stoppingToken);
                if (outcome == DeploymentRunOutcome.Idle) await Task.Delay(Idle, stoppingToken);
                else logger.LogInformation("Deployment worker {WorkerId}: {Outcome}", id, outcome);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Exception type only: messages may contain SQL or tool output.
                logger.LogError("Deployment worker {WorkerId}: processing failed; exception type {ExceptionType}", id, ex.GetType().FullName);
                try { await Task.Delay(Backoff, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
