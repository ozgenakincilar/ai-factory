using System.Security.Claims;
using Factory.Core.Authorization;
using Factory.Infrastructure.Authorization;
using Factory.Infrastructure.Reviews;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Factory.Api.Security;

// WebClient (optional): the Product Workspace's public PKCE client; its access tokens identify humans
// exactly like HumanClient tokens (same hardcoded factory_kind mapper, same audience).
public sealed record FactoryAuthSettings(string Authority, string Audience, string HumanClient, string ServiceClient, bool LocalHttp,
    string? WebClient = null)
{
    public static FactoryAuthSettings Read(IConfiguration configuration, IHostEnvironment environment)
    {
        var authority = configuration["Authentication:Authority"] ?? "";
        var audience = configuration["Authentication:Audience"] ?? "";
        var human = configuration["Authentication:HumanClient"] ?? "";
        var service = configuration["Authentication:ServiceClient"] ?? "";
        var local = configuration.GetValue<bool>("Authentication:AllowDevelopmentLoopbackHttp");
        var web = configuration["Authentication:WebClient"];
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (local ? !environment.IsDevelopment() || authority != "http://127.0.0.1:58080/realms/factory-dev"
                   : uri.Scheme != "https") ||
            string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(human) ||
            string.IsNullOrWhiteSpace(service) || human == service || audience == human || audience == service ||
            (web is not null && (string.IsNullOrWhiteSpace(web) || web == human || web == service || web == audience)))
            throw new InvalidOperationException("invalid_factory_authentication_configuration");
        return new(authority, audience, human, service, local, web);
    }

    // Keycloak access tokens have payload typ=Bearer; ID tokens have typ=ID.
    // factory_kind is a hardcoded, administrator-controlled, access-token-only client mapper.
    public TrustedIdentity? Identity(ClaimsPrincipal user)
    {
        static string? One(ClaimsPrincipal p, string type)
        {
            var claims = p.FindAll(type).ToArray();
            return claims.Length == 1 && !string.IsNullOrWhiteSpace(claims[0].Value) ? claims[0].Value : null;
        }
        if (user.Identity?.IsAuthenticated != true || One(user, "iss") != Authority || One(user, "typ") != "Bearer") return null;
        var subject = One(user, "sub");
        var client = One(user, "azp");
        var kind = One(user, "factory_kind");
        if (subject is null || subject.Length > 255) return null;
        if ((client == HumanClient || (WebClient is not null && client == WebClient)) && kind == "Human") return new(Authority, subject, PrincipalKind.Human);
        if (client == ServiceClient && kind == "Service") return new(Authority, subject, PrincipalKind.Service);
        return null;
    }
}

public static class FactoryAuthentication
{
    public static void AddFactorySecurity(this WebApplicationBuilder builder)
    {
        var settings = FactoryAuthSettings.Read(builder.Configuration, builder.Environment);
        var connection = builder.Configuration.GetConnectionString("Authorization");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("authorization_storage_not_configured");
        builder.Services.AddSingleton(settings);
        // Framework authentication exception text may contain untrusted token material.
        // Emit only our static diagnostic codes and request trace IDs.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.IdentityModel", LogLevel.None);
        builder.Services.AddSingleton<IProjectAccessStore>(new ProjectAccessStore(
            new DbContextOptionsBuilder<ReviewDbContext>().UseNpgsql(connection).Options));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = settings.Authority;
            options.Audience = settings.Audience;
            options.RequireHttpsMetadata = !settings.LocalHttp;
            options.MapInboundClaims = false;
            options.SaveToken = false;
            options.IncludeErrorDetails = false;
            options.TokenValidationParameters = new()
            {
                ValidateIssuer = true, ValidIssuer = settings.Authority,
                ValidateAudience = true, ValidAudience = settings.Audience,
                ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                ValidateLifetime = true, RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(30), ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidTypes = ["JWT"], LogValidationExceptions = false
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    if (settings.Identity(context.Principal!) is null) context.Fail("invalid_access_token_profile");
                    return Task.CompletedTask;
                },
                OnAuthenticationFailed = context =>
                {
                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("FactoryAuthentication").LogWarning("Access token validation failed; request {TraceId}", context.HttpContext.TraceIdentifier);
                    context.NoResult(); // Safe generic challenge; never log token/exception details here.
                    return Task.CompletedTask;
                }
            };
        });
        builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddScoped<IAuthorizationHandler, ProjectAuthorizationHandler>();
    }
}
