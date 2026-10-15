using System.Text.Json;
using System.Threading.Channels;
using Factory.Api;
using Factory.Core.Orchestration;
using Factory.Infrastructure.Orchestration;
using Factory.Orchestration.Durable;
using Factory.Tests.Integration;
using Factory.Tests.Lifecycle;
using Factory.Tests.Reviews;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Factory.Tests.Orchestration;

public class DurableWorkflowEventParsingTests
{
    [Theory]
    [InlineData("""{"projectId":"p","runId":"00000000-0000-0000-0000-000000000000","status":"Queued"}""")]
    [InlineData("""{"projectId":"","runId":"6f0c6a3e-7a52-4d0e-9f1e-5d6c1b2a7e01","status":"Queued"}""")]
    [InlineData("""{"projectId":"p","runId":"6f0c6a3e-7a52-4d0e-9f1e-5d6c1b2a7e01","status":"Exploded"}""")]
    [InlineData("""{"projectId":"p","runId":"6f0c6a3e-7a52-4d0e-9f1e-5d6c1b2a7e01","status":99}""")]
    [InlineData("not json")]
    public void MalformedNotificationsAreIgnored(string payload) => Assert.Null(PostgresRunEventListener.Parse(payload));

    [Fact]
    public void ValidNotificationParses()
        => Assert.Equal(new RunChanged("p", Guid.Parse("6f0c6a3e-7a52-4d0e-9f1e-5d6c1b2a7e01"), RunStatus.Waiting),
            PostgresRunEventListener.Parse("""{"projectId":"p","runId":"6f0c6a3e-7a52-4d0e-9f1e-5d6c1b2a7e01","status":"Waiting"}"""));
}

[Collection("Lifecycle database")]
public class DurableWorkflowEventTests
{
    [OrchestrationFact]
    public async Task CommittedRunChangesNotifyWithMinimalPayloadOnly()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        await using var db = new NpgsqlConnection(LifecycleFixture.Runtime); await db.OpenAsync();
        var payloads = new List<string>(); db.Notification += (_, e) => payloads.Add(e.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN orchestration_runs", db)) await listen.ExecuteNonQueryAsync();
        var key = Guid.NewGuid(); var run = await Durable.Submit(f, key);
        // A rejected (rolled back) admission must not notify.
        await Assert.ThrowsAsync<OrchestrationException>(() => Durable.Api.SubmitAsync(f.Actor, key, Durable.Request(f) with { Context = "different" }));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !payloads.Any(p => p.Contains(run.RunId.ToString()))) await db.WaitAsync(TimeSpan.FromMilliseconds(200));
        var mine = payloads.Select(PostgresRunEventListener.Parse).OfType<RunChanged>().Where(c => c.ProjectId == f.Project).ToArray();
        Assert.Equal(new RunChanged(f.Project, run.RunId, RunStatus.Queued), Assert.Single(mine));
        Assert.All(payloads, p => Assert.DoesNotContain(ReviewSamples.Request.Task, p));
        Assert.All(payloads, p => Assert.Equal(["projectId", "runId", "status"], JsonDocument.Parse(p).RootElement.EnumerateObject().Select(x => x.Name).Order()));
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task WatchersReceiveTheirProjectsCommittedChangesWithoutPolling()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        using var factory = Factory();
        await factory.Services.GetRequiredService<WorkflowEventRelay>().Listening.WaitAsync(TimeSpan.FromSeconds(15));
        await using var watcher = Connection(factory, TestSecurity.Token(subject: f.Actor.Subject));
        var events = Channel.CreateUnbounded<(Guid Run, string Status)>();
        watcher.On<JsonElement>(WorkflowHub.RunChangedMethod, e => events.Writer.TryWrite((e.GetProperty("runId").GetGuid(), e.GetProperty("status").GetString()!)));
        await watcher.StartAsync();
        await watcher.InvokeAsync("WatchProject", f.Project);
        var denied = await Assert.ThrowsAsync<HubException>(() => watcher.InvokeAsync("WatchProject", other.Project));
        Assert.Contains("workflow_forbidden", denied.Message);

        RunSubmission? foreign = null;
        var run = await Durable.Submit(f);
        await Expect(run.RunId, "queued");
        foreign = await Durable.Submit(other);
        Assert.Equal(StepOutcome.Completed, await new DurableWorkflowProcessor(Durable.Worker, new Durable.Provider()).ProcessNextAsync("worker", default));
        await Expect(run.RunId, "running");
        await Durable.Api.CancelAsync(f.Actor, f.Project, run.RunId);
        await Expect(run.RunId, "cancelled");
        await Durable.Api.CancelAsync(other.Actor, other.Project, foreign.RunId);
        await Task.Delay(500);
        while (events.Reader.TryRead(out var extra)) Assert.NotEqual(foreign!.RunId, extra.Run);

        async Task Expect(Guid id, string status)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var next = await events.Reader.ReadAsync(timeout.Token);
                Assert.NotEqual(foreign?.RunId, next.Run);
                if (next.Run == id && next.Status == status) return;
            }
        }
    }

    [OrchestrationFact]
    public async Task AnonymousAndUnmappedTokensCannotConnectOrWatch()
    {
        await using var f = await LifecycleFixture.Create(); using var factory = Factory();
        await using var anonymous = Connection(factory, null);
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
        await using var stranger = Connection(factory, TestSecurity.Token(subject: "not-a-factory-member"));
        await stranger.StartAsync();
        Assert.Contains("workflow_forbidden", (await Assert.ThrowsAsync<HubException>(() => stranger.InvokeAsync("WatchProject", f.Project))).Message);
    }

    private static WebApplicationFactory<Program> Factory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenAI:ApiKey", "sk-test-placeholder-not-a-real-key-1234567890");
            builder.UseSetting("ConnectionStrings:Lifecycle", LifecycleFixture.Runtime);
            TestSecurity.Configure(builder);
            builder.ConfigureServices(services => { services.RemoveAll<IWorkflowRunStore>(); services.AddSingleton<IWorkflowRunStore>(Durable.Api); });
        });

    private static HubConnection Connection(WebApplicationFactory<Program> factory, string? token)
        => new HubConnectionBuilder().WithUrl(new Uri(factory.Server.BaseAddress, WorkflowHub.Path.TrimStart('/')), options =>
        {
            // TestServer supports long polling; the token travels in the Authorization header.
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            if (token is not null) options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        }).Build();
}
