using System.ClientModel;
using System.Net.Http.Json;
using System.Text.Json;
using Factory.Core.Models;
using Factory.Core.Workflows;
using Factory.Tests.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Factory.Tests.Integration;

public class WorkflowEndpointTests
{
    [Fact]
    public async Task RequestCancellation_ReachesProviderAndLogsCorrelatedCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new WorkflowProvider { Handler = async (_, _, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            throw new InvalidOperationException();
        }};
        using var factory = new WorkflowFactory(provider);
        using var client = factory.CreateClient();
        using var cts = new CancellationTokenSource();
        var pending = client.PostAsJsonAsync("/api/workflows/analysis-architecture", WorkflowTests.Request, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        // The disconnected client cannot be expected to receive the 499 body.
        await factory.Logs.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(factory.Logs.Messages, message => message.Contains("Cancelled") && message.Contains("499"));
        Assert.Single(provider.Requests);
    }

    [Theory]
    [InlineData("success", 200, 2)]
    [InlineData("input", 400, 0)]
    [InlineData("refusal", 422, 1)]
    [InlineData("malformed", 502, 1)]
    [InlineData("architectRefusal", 422, 2)]
    [InlineData("architectMalformed", 502, 2)]
    [InlineData("sdk", 502, 2)]
    [InlineData("unexpected", 500, 2)]
    [InlineData("emptyRequirements", 422, 1)]
    public async Task Http_ReturnsCorrelatedSafeResults(string scenario, int expectedStatus, int calls)
    {
        var provider = new WorkflowProvider { Handler = (call, _, _) =>
        {
            if (scenario == "emptyRequirements") return Task.FromResult(new ModelResponse(
                JsonSerializer.Serialize(Factory.Tests.Architecture.ArchitectSamples.Request.Analysis! with
                { FunctionalRequirements = [] }, Factory.Core.Analysis.AnalysisJson.Options), "synthetic"));
            if (call == 2 && scenario == "sdk") throw new ClientResultException("private upstream detail");
            if (call == 2 && scenario == "unexpected") throw new InvalidOperationException("private upstream detail");
            return Task.FromResult((call, scenario) switch
            {
                (1, "refusal") or (2, "architectRefusal") => new ModelResponse("private upstream detail", "synthetic", ModelResponseStatus.Refused),
                (1, "malformed") or (2, "architectMalformed") => new ModelResponse("private upstream detail", "synthetic"),
                _ => WorkflowProvider.Valid(call)
            });
        }};
        using var factory = new WorkflowFactory(provider);
        using var client = factory.CreateClient();
        var request = scenario == "input" ? WorkflowTests.Request with { Task = "" } : WorkflowTests.Request;
        using var response = await client.PostAsJsonAsync("/api/workflows/analysis-architecture", request);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private upstream detail", text);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var result = JsonSerializer.Deserialize<WorkflowResult>(text, options)!;
        Assert.NotEqual(Guid.Empty, result.WorkflowId);
        Assert.Equal(result.WorkflowId.ToString(), response.Headers.GetValues("X-Workflow-Id").Single());
        Assert.Equal(calls, provider.Requests.Count);
        Assert.Equal(expectedStatus == 200, result.Success);
        Assert.Equal(calls == 2 || scenario == "emptyRequirements", result.Analysis is not null);
        Assert.Equal(expectedStatus == 200, result.Architecture is not null);
        Assert.Contains(factory.Logs.Messages, message => message.Contains(result.WorkflowId.ToString()));
        Assert.DoesNotContain(factory.Logs.Messages, message => message.Contains("private upstream detail"));
    }

    private sealed class WorkflowFactory(IModelProvider provider) : WebApplicationFactory<Program>
    {
        internal CaptureLogs Logs { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("OpenAI:ApiKey", "synthetic-test-credential");
            builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(Logs));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IModelProvider>();
                services.AddSingleton(provider);
            });
        }
    }

    private sealed class CaptureLogs : ILoggerProvider, ILogger
    {
        internal System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Enqueue(message);
            if (message.Contains("Cancelled") && message.Contains("499")) Cancelled.TrySetResult();
        }
    }
}
