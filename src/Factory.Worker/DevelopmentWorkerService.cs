using Factory.Orchestration.Development;

namespace Factory.Worker;

// One development job at a time per worker: each job makes paid model calls and builds a whole
// application in a sandbox. Database leases decide ownership; a lost worker ends its job.
public sealed class DevelopmentWorkerService(DevelopmentProcessor processor, ILogger<DevelopmentWorkerService> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var id = $"{Environment.MachineName}:{Environment.ProcessId}:development:{Guid.NewGuid():N}";
        logger.LogInformation("Development worker {WorkerId} started", id);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var outcome = await processor.ProcessNextAsync(id, stoppingToken);
                if (outcome == DevelopmentOutcome.Idle) await Task.Delay(Idle, stoppingToken);
                else logger.LogInformation("Development worker {WorkerId}: job {Outcome}", id, outcome);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Exception type only: messages may contain SQL, model output or sandbox output.
                logger.LogError("Development worker {WorkerId}: job processing failed; exception type {ExceptionType}", id, ex.GetType().FullName);
                try { await Task.Delay(Backoff, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
