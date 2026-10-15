using System.Text.Json;
using Factory.Api.Security;
using Factory.Core.Orchestration;
using Factory.Infrastructure.Orchestration;
using Microsoft.AspNetCore.SignalR;

namespace Factory.Api;

// Real-time run change hints. Clients call WatchProject, then re-read run state over HTTP when
// "runChanged" arrives. Tokens are accepted only in the Authorization header (no query string).
public sealed class WorkflowHub(IWorkflowRunStore store, FactoryAuthSettings settings) : Hub
{
    public const string Path = "/hubs/workflows";
    public const string RunChangedMethod = "runChanged";
    internal static string Group(string projectId) => "project:" + projectId;

    public async Task WatchProject(string projectId)
    {
        var actor = Context.User is { } user ? settings.Identity(user) : null;
        var http = Context.GetHttpContext();
        if (actor is null || (settings.LocalHttp && (http?.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address))))
            throw new HubException("workflow_forbidden");
        try { await store.AuthorizeWatchAsync(actor, projectId, Context.ConnectionAborted); }
        catch (OrchestrationException ex) when (ex.Error is OrchestrationError.Forbidden or OrchestrationError.NotFound)
        { throw new HubException("workflow_forbidden"); }
        catch (OrchestrationException) { throw new HubException("workflow_unavailable"); }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(projectId), Context.ConnectionAborted);
    }

    public Task UnwatchProject(string projectId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(projectId));
}

// Relays committed run changes to watching clients; reconnects with backoff after failures.
public sealed class WorkflowEventRelay(PostgresRunEventListener listener, IHubContext<WorkflowHub> hub, ILogger<WorkflowEventRelay> logger)
    : BackgroundService
{
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(5);
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Listening => _listening.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!listener.Configured) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await listener.ListenAsync(change => hub.Clients.Group(WorkflowHub.Group(change.ProjectId)).SendAsync(WorkflowHub.RunChangedMethod,
                    new { runId = change.RunId, status = JsonNamingPolicy.CamelCase.ConvertName(change.Status.ToString()) }, stoppingToken),
                    () => _listening.TrySetResult(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Exception type only: messages may contain connection or SQL details.
                logger.LogWarning("Workflow event relay disconnected; exception type {ExceptionType}", ex.GetType().FullName);
                try { await Task.Delay(Backoff, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
