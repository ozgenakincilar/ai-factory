using Factory.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Factory.Tests.Integration;

public class ConfigurationPrecedenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperatorConfiguration_OverridesConflictingDevelopmentSecrets(bool commandLine)
    {
        using var factory = new PrecedenceFactory(commandLine);
        using var application = factory.CreateApplication();
        var configuration = application.Services.GetRequiredService<IConfiguration>();
        var settings = application.Services.GetRequiredService<FactoryAuthSettings>();
        var expected = commandLine ? "cli" : "env";
        Assert.Equal($"https://{expected}.factory.invalid/realms/test", settings.Authority);
        Assert.Equal(expected + "-api", settings.Audience);
        Assert.Equal(expected + "-human", settings.HumanClient);
        Assert.Equal(expected + "-service", settings.ServiceClient);
        Assert.False(settings.LocalHttp);
        Assert.Equal($"Host={expected}.invalid;Database={expected};Password=synthetic-{expected}", configuration.GetConnectionString("Authorization"));
        // The precedence test uses only the synthetic replacement secret source.
        Assert.DoesNotContain(((IConfigurationRoot)configuration).Providers.OfType<JsonConfigurationProvider>(),
            provider => provider.Source.Path == "secrets.json");
    }

    [Theory]
    [InlineData("Authentication:Authority", "http://remote.invalid", "invalid_factory_authentication_configuration")]
    [InlineData("ConnectionStrings:Authorization", " ", "authorization_storage_not_configured")]
    public void InvalidOperatorOverride_FailsClosedInsteadOfFallingBackToSecrets(string key, string value, string error)
    {
        using var factory = new PrecedenceFactory(false, key, value);
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateApplication());
        Assert.Equal(error, exception.Message);
    }

    // Exercise default framework provider ordering with synthetic secret values and a unique
    // environment prefix; no developer secret file is changed. Entry-point ordering is checked separately.
    private sealed class PrecedenceFactory(bool commandLine, string? overrideKey = null, string? overrideValue = null)
        : IDisposable
    {
        private readonly string prefix = "FACTORY_PRECEDENCE_" + Guid.NewGuid().ToString("N") + "_";
        private readonly List<string> environmentNames = [];

        public WebApplication CreateApplication()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development", ApplicationName = typeof(Program).Assembly.FullName
            });
            builder.Logging.ClearProviders();
            var configuration = builder.Configuration;
            {
                var sources = configuration.Sources;
                var secretSources = sources.OfType<JsonConfigurationSource>().Where(source => source.Path == "secrets.json").ToArray();
                Assert.Single(secretSources);
                var index = sources.IndexOf(secretSources[0]);
                sources.RemoveAt(index);
                sources.Insert(index, new MemoryConfigurationSource { InitialData = Values("secrets") });
                // Replace the default unprefixed environment provider in-place with an isolated
                // real environment provider. Keep host/environment selection providers intact.
                var environment = Assert.Single(sources.OfType<EnvironmentVariablesConfigurationSource>(),
                    source => string.IsNullOrEmpty(source.Prefix));
                index = sources.IndexOf(environment);
                var values = Values("env");
                if (overrideKey is not null) values[overrideKey] = overrideValue;
                foreach (var (key, value) in values)
                {
                    var name = prefix + key.Replace(":", "__");
                    Environment.SetEnvironmentVariable(name, value);
                    environmentNames.Add(name);
                }
                sources[index] = new EnvironmentVariablesConfigurationSource { Prefix = prefix };
                if (commandLine)
                    sources.Add(new CommandLineConfigurationSource { Args = Values("cli").Select(pair => $"--{pair.Key}={pair.Value}").ToArray() });
            }
            builder.AddFactorySecurity();
            return builder.Build();
        }

        private static Dictionary<string, string?> Values(string origin) => new()
        {
            ["Authentication:Authority"] = $"https://{origin}.factory.invalid/realms/test",
            ["Authentication:Audience"] = origin + "-api",
            ["Authentication:HumanClient"] = origin + "-human",
            ["Authentication:ServiceClient"] = origin + "-service",
            ["Authentication:AllowDevelopmentLoopbackHttp"] = "false",
            ["ConnectionStrings:Authorization"] = $"Host={origin}.invalid;Database={origin};Password=synthetic-{origin}",
            ["OpenAI:ApiKey"] = "synthetic-test-credential"
        };

        public void Dispose()
        {
            foreach (var name in environmentNames) Environment.SetEnvironmentVariable(name, null);
        }
    }
}
