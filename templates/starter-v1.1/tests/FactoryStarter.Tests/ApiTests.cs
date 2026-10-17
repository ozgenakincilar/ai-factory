using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FactoryStarter.Tests;

// These run without PostgreSQL: liveness needs no database and readiness reports it unreachable.
public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task HealthReturnsOk()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReadinessReportsAnUnreachableDatabase()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("unreachable", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownApiPathsAreNotFound()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/does-not-exist")).StatusCode);
    }
}
