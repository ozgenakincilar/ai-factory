using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Factory.Api.Security;
using Factory.Core.Authorization;
using Factory.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Factory.Tests.Integration;

public class AuthenticationTests
{
    private static readonly string[] Routes = ["/api/agents/analyst", "/api/agents/architect", "/api/workflows/analysis-architecture"];

    [Fact]
    public async Task DevelopmentHttpIssuer_CannotAuthorizeNonLoopbackTransport()
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        var context = Microsoft.AspNetCore.Http.EndpointFilterInvocationContext.Create(http,
            new Factory.Core.Agents.AgentRequest("allowed", "task", "context"));
        var filter = new ProjectGenerationFilter(null!, new("http://127.0.0.1:58080/realms/factory-dev", "factory-api", "human", "service", true),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGenerationFilter>.Instance);
        var result = await filter.InvokeAsync(context, _ => throw new InvalidOperationException("must_not_execute"));
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>(result);
    }

    [Fact]
    public async Task UnavailableSigningMetadata_FailsClosed()
    {
        using var factory = new SecurityFactory(new TestAccessStore(), metadataUnavailable: true);
        using var client = TestSecurity.Client(factory);
        var response = await client.PostAsJsonAsync(Routes[0], new { projectId = "allowed", task = "test", context = "test" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Provider.Calls);
        Assert.DoesNotContain(factory.Logs.Messages, message => message.Contains("private_metadata_failure"));
    }

    private sealed class UnavailableMetadata : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("private_metadata_failure");
        public void RequestRefresh() { }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("unsigned")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("idToken")]
    [InlineData("subject")]
    [InlineData("duplicate")]
    [InlineData("serviceAsHuman")]
    [InlineData("humanAsService")]
    [InlineData("unknownClient")]
    [InlineData("noExpiration")]
    [InlineData("missingKind")]
    public async Task InvalidTokens_RejectAllGenerationRoutes_WithoutModelCalls(string scenario)
    {
        using var other = RSA.Create(2048);
        var token = scenario switch
        {
            "missing" => null, "malformed" => "not-a-token",
            "issuer" => TestSecurity.Token(issuer: "https://attacker.invalid"),
            "audience" => TestSecurity.Token(audience: "other-api"),
            "signature" => TestSecurity.Token(key: new RsaSecurityKey(other)),
            "unsigned" => TestSecurity.Token(unsigned: true),
            "expired" => TestSecurity.Token(expires: -60, notBefore: -120),
            "future" => TestSecurity.Token(notBefore: 120),
            "idToken" => TestSecurity.Token(type: "ID"), // Same issuer, signature AND API audience.
            "subject" => TestSecurity.Token(subject: ""),
            "duplicate" => TestSecurity.Token(duplicateSubject: true),
            "serviceAsHuman" => TestSecurity.Token(client: "factory-agent", kind: "Human"),
            "humanAsService" => TestSecurity.Token(kind: "Service"),
            "noExpiration" => TestSecurity.Token(noExpiration: true),
            "missingKind" => TestSecurity.Token(kind: ""),
            _ => TestSecurity.Token(client: "unknown")
        };
        using var factory = new SecurityFactory(new TestAccessStore { OnlyProject = "allowed" });
        using var client = factory.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        foreach (var route in Routes)
        {
            var response = await client.PostAsJsonAsync(route, new { projectId = "allowed", task = "test", context = "test", reviewerId = "human-1" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Bearer");
            Assert.DoesNotContain("not-a-token", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, factory.Provider.Calls);
        if (token is not null) Assert.DoesNotContain(factory.Logs.Messages, entry => entry.Contains(token));
    }

    [Theory]
    [InlineData("other", true, true, "human-1", 403)]
    [InlineData("allowed", false, true, "human-1", 403)]
    [InlineData("allowed", true, true, "unknown", 403)]
    [InlineData("allowed", true, false, "human-1", 503)]
    public async Task ResourceDenials_FailClosed(string project, bool active, bool available, string subject, int expected)
    {
        using var factory = new SecurityFactory(new TestAccessStore { OnlyProject = "allowed", Active = active, Available = available });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestSecurity.Token(subject: subject));
        foreach (var route in Routes)
        {
            var response = await client.PostAsJsonAsync(route, new { projectId = project, task = "test", context = "test", reviewerId = "human-1", role = "Reviewer" });
            Assert.Equal(expected, (int)response.StatusCode);
            Assert.DoesNotContain("private_database_details", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, factory.Provider.Calls);
        Assert.DoesNotContain(factory.Logs.Messages, entry => entry.Contains("private_database_details"));
    }

    [Fact]
    public async Task HealthRemainsAnonymous_AndApprovalRouteDoesNotExist()
    {
        using var factory = new SecurityFactory(new TestAccessStore());
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestSecurity.Token());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/reviews/approve", new { })).StatusCode);
        Assert.Equal(0, factory.Provider.Calls);
    }

    [Fact]
    public async Task ServiceReviewer_IsDeniedEvenWhenStoreWouldAllow()
    {
        var settings = new FactoryAuthSettings(TestSecurity.Issuer, "factory-api", "factory-dev-cli", "factory-agent", false);
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("iss", TestSecurity.Issuer), new("sub", "service"),
            new("typ", "Bearer"), new("azp", "factory-agent"), new("factory_kind", "Service"), new("role", "Reviewer") }, "Bearer"));
        var store = new AllowStore();
        var requirement = new ProjectRequirement(ProjectRole.Reviewer);
        var context = new AuthorizationHandlerContext([requirement], user, new ProjectResource("allowed", default));
        await new ProjectAuthorizationHandler(store, settings).HandleAsync(context);
        Assert.False(context.HasSucceeded);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("Production", "http://127.0.0.1:58080/realms/factory-dev", true)]
    [InlineData("Development", "http://localhost:58080/realms/factory-dev", true)]
    [InlineData("Development", "http://192.168.1.1:58080/realms/factory-dev", true)]
    [InlineData("Development", "http://127.0.0.1:58080/realms/factory-dev", false)]
    [InlineData("Production", "", false)]
    public void InsecureOrMissingConfiguration_IsRejected(string environment, string authority, bool local)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Authentication:Authority"] = authority, ["Authentication:Audience"] = "factory-api",
            ["Authentication:HumanClient"] = "human", ["Authentication:ServiceClient"] = "service",
            ["Authentication:AllowDevelopmentLoopbackHttp"] = local.ToString() }).Build();
        Assert.Throws<InvalidOperationException>(() => FactoryAuthSettings.Read(config, new TestEnvironment { EnvironmentName = environment }));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "";
        public string ApplicationName { get; set; } = "";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
    private sealed class AllowStore : IProjectAccessStore
    {
        public int Calls;
        public Task<bool> HasAccessAsync(TrustedIdentity identity, string projectId, ProjectRole role, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(true); }
    }
    internal sealed class CountingProvider : IModelProvider
    {
        public int Calls;
        public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Unexpected model invocation"); }
    }
    internal sealed class SecurityFactory(IProjectAccessStore store, bool metadataUnavailable = false) : WebApplicationFactory<Program>
    {
        internal CountingProvider Provider { get; } = new();
        internal SafeLogs Logs { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("OpenAI:ApiKey", "synthetic-test-credential");
            builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(Logs));
            TestSecurity.Configure(builder, store);
            if (metadataUnavailable) builder.ConfigureServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager = new UnavailableMetadata()));
            builder.ConfigureServices(services => { services.RemoveAll<IModelProvider>(); services.AddSingleton<IModelProvider>(Provider); });
        }
    }

    internal sealed class SafeLogs : ILoggerProvider, ILogger
    {
        internal System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Dispose() { }
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception) + exception?.ToString());
    }
}
