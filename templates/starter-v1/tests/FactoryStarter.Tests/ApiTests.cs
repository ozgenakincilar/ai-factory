using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FactoryStarter.Tests;

// The health endpoint needs no database, so this runs without PostgreSQL.
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
}
