using System.ClientModel;
using System.Net.Http.Json;
using Factory.Core.Models;
using Factory.Tests.Architecture;
using Factory.Tests.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Factory.Tests.Integration;

public class SecurityHardeningTests
{
    private const string Sensitive = "private-prompt Bearer synthetic-token Password=private-password sk-synthetic-secret";

    [Theory]
    [InlineData("analyst", false, 500)]
    [InlineData("architect", false, 500)]
    [InlineData("workflow", false, 500)]
    [InlineData("analyst", true, 502)]
    [InlineData("architect", true, 502)]
    [InlineData("workflow", true, 502)]
    public async Task Failures_LogClassificationAndCorrelation_WithoutExceptionPayloads(string operation, bool sdk, int status)
    {
        using var factory = new HardeningFactory(sdk);
        using var client = TestSecurity.Client(factory);
        // Exclude host startup diagnostics (including machine-specific Data Protection setup).
        factory.Logs.Messages.Clear();
        factory.Logs.Exceptions.Clear();
        var route = operation == "workflow" ? "/api/workflows/analysis-architecture" : "/api/agents/" + operation;
        using var response = operation == "architect"
            ? await client.PostAsJsonAsync(route, ArchitectSamples.Request with { Task = Sensitive }, Factory.Core.Analysis.AnalysisJson.Options)
            : await client.PostAsJsonAsync(route, WorkflowTests.Request with { Task = Sensitive });
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(1, factory.Provider.Calls);
        var requestId = response.Headers.GetValues("X-Request-Id").Single();
        Assert.Contains(factory.Logs.Messages, entry => entry.Contains(requestId) &&
            entry.Contains(sdk ? "provider_failure" : "unexpected_") &&
            entry.Contains(sdk ? nameof(ClientResultException) : nameof(InvalidOperationException)));
        if (operation == "workflow")
            Assert.Contains(factory.Logs.Messages, entry => entry.Contains(response.Headers.GetValues("X-Workflow-Id").Single()));
        Assert.DoesNotContain(Sensitive, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(factory.Logs.Messages, entry => entry.Contains("private-") || entry.Contains("synthetic-token") || entry.Contains("synthetic-secret"));
        Assert.Empty(factory.Logs.Exceptions);
    }

    [Theory]
    [InlineData("Testing", "/api/agents/analyst")]
    [InlineData("Development", "/api/agents/architect")]
    [InlineData("Development", "/api/workflows/analysis-architecture")]
    public async Task MalformedBodies_AreSafeEvenInDevelopment(string environment, string route)
    {
        using var factory = new HardeningFactory(false, environment);
        using var client = TestSecurity.Client(factory);
        // Exclude host startup diagnostics (including machine-specific Data Protection setup).
        factory.Logs.Messages.Clear();
        factory.Logs.Exceptions.Clear();
        // A JSON property name can appear in a deserialization exception's Path.
        using var response = await client.PostAsync(route,
            new StringContent("{\"private-prompt\": [", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal(0, factory.Provider.Calls);
        Assert.DoesNotContain("private-prompt", await response.Content.ReadAsStringAsync());
        Assert.Contains(factory.Logs.Messages, entry => entry.Contains("invalid_http_request") &&
            entry.Contains(response.Headers.GetValues("X-Request-Id").Single()));
        Assert.DoesNotContain(factory.Logs.Messages, entry => entry.Contains("private-prompt"));
        Assert.Empty(factory.Logs.Exceptions);
    }

    [Fact]
    public async Task DependencyActivationFailure_IsSafelyHandledOutsideAgentHandler()
    {
        using var factory = new HardeningFactory(false, "Development", activationFailure: true);
        using var client = TestSecurity.Client(factory);
        // Exclude host startup diagnostics (including machine-specific Data Protection setup).
        factory.Logs.Messages.Clear();
        factory.Logs.Exceptions.Clear();
        using var response = await client.PostAsJsonAsync("/api/agents/analyst", WorkflowTests.Request);
        Assert.Equal(500, (int)response.StatusCode);
        Assert.DoesNotContain(Sensitive, await response.Content.ReadAsStringAsync());
        Assert.Contains(factory.Logs.Messages, entry => entry.Contains("unexpected_api_failure"));
        Assert.DoesNotContain(factory.Logs.Messages, entry => entry.Contains("private-"));
        Assert.Empty(factory.Logs.Exceptions);
    }

    [Fact]
    public void DevelopmentEntryPoint_DoesNotAppendSecretsAfterEnvironment()
    {
        using var factory = new HardeningFactory(false, "Development");
        using var client = factory.CreateClient();
        var providers = ((IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>()).Providers.ToList();
        var environmentIndex = providers.FindLastIndex(provider => provider is EnvironmentVariablesConfigurationProvider);
        Assert.True(environmentIndex >= 0);
        Assert.DoesNotContain(providers.Skip(environmentIndex + 1).OfType<JsonConfigurationProvider>(),
            provider => provider.Source.Path == "secrets.json");
    }

    private sealed class ThrowingProvider(bool sdk) : IModelProvider
    {
        internal int Calls;
        public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (sdk) throw new ClientResultException(Sensitive);
            var exception = new InvalidOperationException(Sensitive, new Exception(Sensitive));
            exception.Data["private-data"] = Sensitive;
            throw exception;
        }
    }

    private sealed class HardeningFactory(bool sdk, string environment = "Testing", bool activationFailure = false) : WebApplicationFactory<Program>
    {
        internal readonly ThrowingProvider Provider = new(sdk);
        internal readonly Capture Logs = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("OpenAI:ApiKey", "synthetic-test-credential");
            TestSecurity.Configure(builder);
            builder.ConfigureLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Debug).AddProvider(Logs));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IModelProvider>();
                services.AddSingleton<IModelProvider>(_ => activationFailure
                    ? throw new InvalidOperationException(Sensitive) : Provider);
            });
        }
    }

    private sealed class Capture : ILoggerProvider, ILogger
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages = new();
        internal readonly System.Collections.Concurrent.ConcurrentQueue<Exception> Exceptions = new();
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Dispose() { }
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(formatter(state, exception));
            if (exception is not null) Exceptions.Enqueue(exception);
        }
    }
}
