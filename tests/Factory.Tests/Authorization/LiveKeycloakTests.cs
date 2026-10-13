using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Factory.Core.Models;
using Factory.Tests.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Factory.Tests.Authorization;

public sealed class LiveKeycloakFactAttribute : FactAttribute
{
    public LiveKeycloakFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_KEYCLOAK_SETTINGS")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_KEYCLOAK_HUMAN_TOKENS")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_AUTH_READER_CONNECTION")))
            Skip = "Requires local Keycloak, browser PKCE evidence, provisioned memberships and PostgreSQL reader.";
    }
}

public class LiveKeycloakTests
{
    private const string Issuer = "http://127.0.0.1:58080/realms/factory-dev";

    [LiveKeycloakFact]
    public async Task ServiceClientCredentials_AreValidatedByApi_AndProjectScoped()
    {
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("FACTORY_KEYCLOAK_SETTINGS")!));
        using var http = new HttpClient();
        var response = await http.PostAsync(Issuer + "/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string,string>
        {
            ["grant_type"]="client_credentials", ["client_id"]="factory-agent",
            ["client_secret"]=settings.RootElement.GetProperty("ServiceSecret").GetString()!
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var tokens = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await VerifyApi(tokens.RootElement.GetProperty("access_token").GetString()!);
        // The same service cannot use a password grant or authorization-code flow.
        var denied = await http.PostAsync(Issuer + "/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string,string>
        {
            ["grant_type"]="password", ["client_id"]="factory-agent",
            ["client_secret"]=settings.RootElement.GetProperty("ServiceSecret").GetString()!, ["username"]="unused", ["password"]="unused"
        }));
        Assert.False(denied.IsSuccessStatusCode);
    }

    [LiveKeycloakFact]
    public async Task RealBrowserHumanTokens_ValidateSignatureAudienceNonce_AndRejectIdTokenAtApi()
    {
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("FACTORY_KEYCLOAK_HUMAN_TOKENS")!));
        var accessToken = evidence.RootElement.GetProperty("accessToken").GetString()!;
        var idToken = evidence.RootElement.GetProperty("idToken").GetString()!;
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(Issuer + "/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = false });
        var metadata = await manager.GetConfigurationAsync(default);
        var user = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(idToken, new TokenValidationParameters
        {
            ValidIssuer = Issuer, ValidAudience = "factory-dev-cli", IssuerSigningKeys = metadata.SigningKeys,
            ValidateIssuer = true, ValidateAudience = true, ValidateIssuerSigningKey = true, ValidateLifetime = true,
            RequireSignedTokens = true, RequireExpirationTime = true, ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.FromSeconds(30)
        }, out _);
        Assert.Equal(evidence.RootElement.GetProperty("nonce").GetString(), user.FindFirst("nonce")?.Value);
        Assert.Equal("ID", user.FindFirst("typ")?.Value);
        await VerifyApi(accessToken);
        using var factory = new LiveFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", idToken);
        var denied = await client.PostAsJsonAsync("/api/workflows/analysis-architecture", WorkflowTests.Request with { ProjectId = "factory-local" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Empty(factory.Provider.Requests);
    }

    private static async Task VerifyApi(string accessToken)
    {
        using var factory = new LiveFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
        var response = await client.PostAsJsonAsync("/api/workflows/analysis-architecture", WorkflowTests.Request with { ProjectId = "factory-local" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, factory.Provider.Requests.Count);
        Assert.All(factory.Provider.Requests, request => Assert.DoesNotContain(accessToken, JsonSerializer.Serialize(request)));
        response = await client.PostAsJsonAsync("/api/workflows/analysis-architecture", WorkflowTests.Request with { ProjectId = "not-a-member" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(2, factory.Provider.Requests.Count);
    }

    private sealed class LiveFactory : WebApplicationFactory<Program>
    {
        internal WorkflowProvider Provider { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Authentication:Authority", Issuer);
            builder.UseSetting("Authentication:Audience", "factory-api");
            builder.UseSetting("Authentication:HumanClient", "factory-dev-cli");
            builder.UseSetting("Authentication:ServiceClient", "factory-agent");
            builder.UseSetting("Authentication:AllowDevelopmentLoopbackHttp", "true");
            builder.UseSetting("ConnectionStrings:Authorization", Environment.GetEnvironmentVariable("FACTORY_AUTH_READER_CONNECTION"));
            builder.UseSetting("OpenAI:ApiKey", "synthetic-test-credential");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IModelProvider>(); services.AddSingleton<IModelProvider>(Provider);
                services.AddSingleton<IStartupFilter, LoopbackTestTransport>();
            });
        }
    }
    // TestServer has no socket. Model its transport address only; authentication/store are real.
    private sealed class LoopbackTestTransport : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return continuation(context); });
            next(app);
        };
    }
}
