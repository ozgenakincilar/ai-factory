using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Factory.Core.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Factory.Tests.Integration;

internal static class TestSecurity
{
    internal const string Issuer = "https://issuer.factory.test/realms/tests";
    internal static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test-key" };

    // Existing regression suites exercise the retired synchronous routes, so they opt back in
    // (allowed only in Development/Testing). Retirement itself is tested with the default.
    internal static void Configure(IWebHostBuilder builder, IProjectAccessStore? store = null, bool legacyGeneration = true)
    {
        if (legacyGeneration) builder.UseSetting(Factory.Api.LegacyGeneration.Setting, "true");
        builder.UseSetting("Authentication:Authority", Issuer);
        builder.UseSetting("Authentication:Audience", "factory-api");
        builder.UseSetting("Authentication:HumanClient", "factory-dev-cli");
        builder.UseSetting("Authentication:ServiceClient", "factory-agent");
        builder.UseSetting("ConnectionStrings:Authorization", "Host=unused;Database=unused");
        builder.UseSetting("ConnectionStrings:Reviews", "Host=unused;Database=unused");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IProjectAccessStore>();
            services.AddSingleton(store ?? new TestAccessStore());
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
                metadata.SigningKeys.Add(Key);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            });
        });
    }

    internal static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        return client;
    }

    internal static string Token(string issuer = Issuer, string audience = "factory-api", string subject = "human-1",
        string client = "factory-dev-cli", string kind = "Human", string type = "Bearer", int expires = 300,
        SecurityKey? key = null, bool unsigned = false, int notBefore = -60, bool duplicateSubject = false, bool noExpiration = false)
    {
        var claims = new List<Claim> { new("sub", subject), new("azp", client), new("factory_kind", kind), new("typ", type),
            new("role", "Reviewer") }; // Intentionally never grants membership.
        if (duplicateSubject) claims.Add(new("sub", "forged-subject"));
        var token = new JwtSecurityToken(issuer, audience, claims, DateTime.UtcNow.AddSeconds(notBefore),
            DateTime.UtcNow.AddSeconds(expires), unsigned ? null : new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256));
        if (noExpiration) token.Payload.Remove("exp");
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

internal sealed class TestAccessStore : IProjectAccessStore
{
    public bool Available { get; set; } = true;
    public bool Active { get; set; } = true;
    public string? OnlyProject { get; set; }
    public Task<bool> HasAccessAsync(TrustedIdentity identity, string projectId, ProjectRole role, CancellationToken cancellationToken = default)
    {
        if (!Available) throw new InvalidOperationException("private_database_details");
        return Task.FromResult(Active && identity.Subject == "human-1" && identity.Kind == PrincipalKind.Human &&
            (OnlyProject is null || OnlyProject == projectId) && role == ProjectRole.Generator);
    }
}
