using Factory.Orchestration.Durable;

namespace Factory.Worker;

// Runs a bounded number of independent claim loops. Database leases, not this process,
// decide ownership; a stopped or crashed worker's steps are reclaimed after lease expiry.
public sealed class WorkflowWorkerService(DurableWorkflowProcessor processor, IConfiguration configuration,
    ILogger<WorkflowWorkerService> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(5);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = Math.Clamp(configuration.GetValue("Worker:Concurrency", 2), 1, 8);
        var id = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        logger.LogInformation("Workflow worker {WorkerId} started with {Loops} loops", id, loops);
        return Task.WhenAll(Enumerable.Range(0, loops).Select(i => Loop($"{id}:{i}", stoppingToken)));
    }

    private async Task Loop(string workerId, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var outcome = await processor.ProcessNextAsync(workerId, stopping);
                if (outcome == StepOutcome.Idle) await Task.Delay(Idle, stopping);
                else logger.LogInformation("Worker {WorkerId}: step {Outcome}", workerId, outcome);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Exception type only: messages may contain SQL, payloads or provider details.
                logger.LogError("Worker {WorkerId}: step processing failed; exception type {ExceptionType}", workerId, ex.GetType().FullName);
                try { await Task.Delay(Backoff, stopping); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
