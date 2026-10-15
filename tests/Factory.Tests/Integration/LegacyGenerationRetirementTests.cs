using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Factory.Api;
using Factory.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Factory.Tests.Integration;

// Retired synchronous generation: unmetered and unbudgeted, so closed by default everywhere.
public class LegacyGenerationRetirementTests
{
    private static readonly string[] Routes = ["/api/agents/analyst", "/api/agents/architect", "/api/workflows/analysis-architecture",
        "/api/projects/allowed/work-items", $"/api/projects/allowed/work-items/{Guid.NewGuid():D}/analysis-architecture"];

    private sealed class Factory(string environment, bool? legacy) : WebApplicationFactory<Program>
    {
        internal AuthenticationTests.CountingProvider Provider { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("OpenAI:ApiKey", "synthetic-test-credential");
            TestSecurity.Configure(builder, legacyGeneration: false);
            if (legacy is { } value) builder.UseSetting(LegacyGeneration.Setting, value.ToString());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IModelProvider>(); services.AddSingleton<IModelProvider>(Provider);
                services.RemoveAllKeyed<IModelProvider>("lifecycle"); services.AddKeyedSingleton<IModelProvider>("lifecycle", Provider);
            });
        }
    }

    [Theory]
    [InlineData("Testing", null)]
    [InlineData("Development", false)]
    public async Task RetiredRoutesAnswerGoneWithoutCallingAModel(string environment, bool? legacy)
    {
        using var factory = new Factory(environment, legacy);
        using var client = TestSecurity.Client(factory);
        using var anonymous = factory.CreateClient();
        foreach (var route in Routes)
        {
            using var response = await client.PostAsJsonAsync(route, new { projectId = "allowed", task = "Build a task list", context = "" });
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("generation_route_retired", problem.RootElement.GetProperty("title").GetString());
            using var unauthenticated = await anonymous.PostAsJsonAsync(route, new { projectId = "allowed" });
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        }
        Assert.Equal(0, factory.Provider.Calls);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ReenablingOutsideDevelopmentOrTestingRefusesToStart(string environment)
    {
        using var factory = new Factory(environment, true);
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("legacy_generation_endpoints_forbidden_outside_development", Flatten(error));
    }

    [Fact]
    public async Task DurableRunsAndLifecycleReadsRemainMapped()
    {
        using var factory = new Factory("Testing", null);
        using var client = TestSecurity.Client(factory);
        // Not 404/405/410: the routes exist and reach their handlers or authorization.
        using var runs = await client.PostAsJsonAsync("/api/projects/allowed/runs", new { });
        Assert.NotEqual(HttpStatusCode.Gone, runs.StatusCode); Assert.NotEqual(HttpStatusCode.NotFound, runs.StatusCode);
        using var read = await client.GetAsync($"/api/projects/allowed/work-items/{Guid.NewGuid():D}");
        Assert.NotEqual(HttpStatusCode.Gone, read.StatusCode); Assert.NotEqual(HttpStatusCode.MethodNotAllowed, read.StatusCode);
    }

    private static string Flatten(Exception error)
    {
        var messages = new List<string>();
        for (Exception? e = error; e is not null; e = e.InnerException) messages.Add(e.Message);
        if (error is AggregateException aggregate) messages.AddRange(aggregate.Flatten().InnerExceptions.Select(e => e.Message));
        return string.Join(" | ", messages);
    }
}
