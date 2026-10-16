using Factory.Orchestration.Development;

namespace Factory.Worker;

// Opens pull requests for verified development jobs. Never merges; humans review and merge on GitHub.
public sealed class PullRequestWorkerService(PullRequestProcessor processor, ILogger<PullRequestWorkerService> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Pull request worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var step = await processor.ProcessNextAsync(stoppingToken);
                if (step != PullRequestStep.Idle) logger.LogInformation("Pull request worker: {Step}", step);
                if (step == PullRequestStep.Idle) await Task.Delay(Idle, stoppingToken);
                else if (step == PullRequestStep.Deferred) await Task.Delay(Backoff, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Exception type only: messages may contain repository or API details.
                logger.LogError("Pull request worker failed; exception type {ExceptionType}", ex.GetType().FullName);
                try { await Task.Delay(Backoff, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
