using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Factory.Core.Authorization;
using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Core.Tenancy;
using Factory.Infrastructure.OpenAI;
using Factory.Infrastructure.Security;
using Factory.Orchestration.Durable;
using Factory.Tests.Integration;
using Factory.Tests.Lifecycle;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Factory.Tests.Orchestration;

[Collection("Lifecycle database")]
public class ModelRegistryTests
{
    private const string CustomerKey = "sk-customer-test-key-0123456789";
    private static readonly AesGcmSecretProtector Protector = new(RandomNumberGenerator.GetBytes(32));
    private static readonly ModelEndpointPolicy Loopback = new(allowLoopback: true);

    private static async Task<RegisteredModel> Register(LifecycleFixture f, FakeOpenAiServer server, long? input = null, long? output = null)
    {
        var id = Guid.NewGuid();
        var registration = new ModelRegistration("model-" + id.ToString("N")[..8], ModelProtocol.OpenAiCompatible, server.BaseUrl, "customer-model",
            CustomerKey, input, output);
        return await Durable.Api.RegisterAsync(f.Actor, f.Organization, id, registration, Protector.Protect(CustomerKey, ModelEndpoint.SecretContext(f.Organization, id)));
    }

    private sealed class FactoryModel : IModelProvider
    {
        internal int Calls;
        public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct = default)
        { Calls++; throw new InvalidOperationException("the Factory default model must not be used"); }
    }

    private static DurableWorkflowProcessor Processor(FactoryModel fallback)
        => new(Durable.Worker, new RegistryModelResolver(Durable.Worker, fallback, new ModelIdentity("openai", "gpt-4o-mini"),
            new CustomerModelProviderFactory(Protector, Loopback)));

    [OrchestrationFact]
    public async Task RegisteredVerifiedModelRunsTheWorkflowWithTheCustomersOwnCredential()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); await using var server = await FakeOpenAiServer.StartAsync();
        var model = await Register(f, server, 1_000_000, 1_000_000);
        Assert.Equal(ModelVerificationStatus.Unverified, model.Status);
        var endpoint = await Durable.Api.GetEndpointAsync(f.Actor, f.Organization, model.Id);
        var (status, structured) = await new ModelVerifier(new CustomerModelProviderFactory(Protector, Loopback)).VerifyAsync(endpoint, default);
        var verified = await Durable.Api.RecordVerificationAsync(f.Actor, f.Organization, model.Id, status, structured);
        Assert.Equal((ModelVerificationStatus.Verified, (bool?)true), (verified.Status, verified.StructuredOutput));
        await Durable.Api.AssignAsync(f.Actor, f.Project, model.Id, acceptCapabilityRisk: false);
        // An organization budget applies; the customer-declared price lets reservations proceed.
        await Durable.Sql("""
            INSERT INTO orchestration."Budgets" VALUES (gen_random_uuid(),'Organization',@o,'Month',1000000,clock_timestamp()-interval '1 second','synthetic test budget')
            """, new NpgsqlParameter("o", f.Organization));
        var run = await Durable.Submit(f); var fallback = new FactoryModel();
        for (var i = 0; i < 3; i++) Assert.Equal(StepOutcome.Completed, await Processor(fallback).ProcessNextAsync("worker", default));
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal(RunStatus.Succeeded, view.Status);
        Assert.Equal((0, 3, "Bearer " + CustomerKey), (fallback.Calls, server.Completions, server.LastAuthorization));
        // Two calls of 11 + 7 tokens at one micro-USD per token, attributed to the customer model.
        Assert.Equal(new UsageTotals(2, 22, 14, 36, 0, 0), view.Usage);
        Assert.Equal("customer:" + model.Id.ToString("N"), Assert.Single((await Durable.Api.GetUsageAsync(f.Actor, f.Project,
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(1))).Models).Provider);
    }

    [OrchestrationFact]
    public async Task UnverifiedOrNonStructuredModelsNeedExplicitCustomerAcceptance()
    {
        await using var f = await LifecycleFixture.Create(); await using var server = await FakeOpenAiServer.StartAsync();
        var unverified = await Register(f, server);
        Assert.Equal(OrchestrationError.CapabilityUnverified, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.AssignAsync(f.Actor, f.Project, unverified.Id, false))).Error);
        server.NoStructuredOutput = true;
        var endpoint = await Durable.Api.GetEndpointAsync(f.Actor, f.Organization, unverified.Id);
        var (status, structured) = await new ModelVerifier(new CustomerModelProviderFactory(Protector, Loopback)).VerifyAsync(endpoint, default);
        Assert.Equal((ModelVerificationStatus.Verified, (bool?)false), (status, structured));
        await Durable.Api.RecordVerificationAsync(f.Actor, f.Organization, unverified.Id, status, structured);
        Assert.Equal(OrchestrationError.CapabilityUnverified, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.AssignAsync(f.Actor, f.Project, unverified.Id, false))).Error);
        var accepted = await Durable.Api.AssignAsync(f.Actor, f.Project, unverified.Id, true);
        Assert.True(accepted.AcceptedCapabilityRisk);
        Assert.Null((await Durable.Api.AssignAsync(f.Actor, f.Project, null, false)).ModelId);
        server.Fail = true;
        var failed = await new ModelVerifier(new CustomerModelProviderFactory(Protector, Loopback)).VerifyAsync(endpoint, default);
        Assert.Equal((ModelVerificationStatus.Failed, (bool?)null), failed);
    }

    [OrchestrationFact]
    public async Task AnUnreachableCustomerModelWaitsForTheCustomerWithoutFallingBack()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); await using var server = await FakeOpenAiServer.StartAsync();
        var model = await Register(f, server);
        await Durable.Api.AssignAsync(f.Actor, f.Project, model.Id, acceptCapabilityRisk: true);
        var run = await Durable.Submit(f);
        // The assignment snapshot stays on the run even if the project switches back afterwards.
        await Durable.Api.AssignAsync(f.Actor, f.Project, null, false);
        server.Fail = true; var fallback = new FactoryModel();
        Assert.Equal(StepOutcome.Suspended, await Processor(fallback).ProcessNextAsync("worker", default));
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal((RunStatus.Waiting, "customer_model_unavailable", 0), (view.Status, view.Decisions![0].Reason, fallback.Calls));
        // A worker without the master key cannot use the model either, and also waits.
        await Durable.Api.DecideAsync(f.Actor, f.Project, run.RunId, view.Decisions[0].Id, "continue");
        var keyless = new DurableWorkflowProcessor(Durable.Worker, new RegistryModelResolver(Durable.Worker, fallback, null, null));
        Assert.Equal(StepOutcome.Suspended, await keyless.ProcessNextAsync("worker", default));
        Assert.Equal(0, fallback.Calls);
        await Durable.Api.CancelAsync(f.Actor, f.Project, run.RunId);
    }

    [OrchestrationFact]
    public async Task ModelsStayInsideTheirTenantAndCredentialsStayEncrypted()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        await using var server = await FakeOpenAiServer.StartAsync();
        var mine = await Register(f, server); var theirs = await Register(other, server);
        async Task<OrchestrationError> Error(Func<Task> action) => (await Assert.ThrowsAsync<OrchestrationException>(action)).Error;
        Assert.Equal(OrchestrationError.Forbidden, await Error(() => Durable.Api.ListAsync(other.Actor, f.Organization)));
        Assert.Equal(OrchestrationError.Forbidden, await Error(() => Durable.Api.GetEndpointAsync(other.Actor, f.Organization, mine.Id)));
        Assert.Equal(OrchestrationError.NotFound, await Error(() => Durable.Api.GetEndpointAsync(f.Actor, f.Organization, theirs.Id)));
        Assert.Equal(OrchestrationError.NotFound, await Error(() => Durable.Api.AssignAsync(f.Actor, f.Project, theirs.Id, true)));
        Assert.Equal(OrchestrationError.Forbidden, await Error(() => Durable.Api.AssignAsync(other.Actor, f.Project, mine.Id, true)));
        Assert.Equal(OrchestrationError.Conflict, await Error(() => Durable.Api.RegisterAsync(f.Actor, f.Organization, Guid.NewGuid(),
            new ModelRegistration(mine.Name, ModelProtocol.OpenAiCompatible, server.BaseUrl, "m", CustomerKey), new byte[40])));
        // Even the owner login cannot point a project at another tenant's model.
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Durable.Sql("""
            INSERT INTO tenancy."ProjectModels" VALUES (gen_random_uuid(),@p,@m,true,@a,now())
            """, new NpgsqlParameter("p", f.Project), new NpgsqlParameter("m", theirs.Id), new NpgsqlParameter("a", f.PrincipalId)))).SqlState);
        // Stored ciphertext only; the API key never appears in plaintext in the database.
        Assert.Equal(0L, await Durable.Scalar("""SELECT count(*) FROM tenancy."Models" WHERE position(convert_to(@k,'UTF8') in "ApiKeyCiphertext")>0""",
            new NpgsqlParameter("k", CustomerKey)));
        // The endpoint read needs a live lease token; runtime roles cannot read credentials directly.
        await Durable.Api.AssignAsync(f.Actor, f.Project, mine.Id, true); await Durable.Submit(f);
        var token = RandomNumberGenerator.GetBytes(32); var step = (await Durable.Worker.ClaimAsync("worker", token))!;
        Assert.Equal(mine.Id, (await Durable.Worker.GetModelEndpointAsync(step.AttemptId, token))!.ModelId);
        Assert.Equal(OrchestrationError.Forbidden, await Error(() => Durable.Worker.GetModelEndpointAsync(step.AttemptId, RandomNumberGenerator.GetBytes(32))));
        foreach (var connection in new[] { LifecycleFixture.Runtime, Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")! })
        {
            await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
            foreach (var sql in new[] { """SELECT "ApiKeyCiphertext" FROM tenancy."Models" """, """SELECT * FROM tenancy."ProjectModels" """ })
            {
                await using var command = new NpgsqlCommand(sql, db);
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            }
        }
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task HttpRegistrationIsAdminOnlyWriteOnlyAndRequiresTheMasterKey()
    {
        await using var f = await LifecycleFixture.Create(); await using var server = await FakeOpenAiServer.StartAsync();
        WebApplicationFactory<Program> Factory(bool withKey) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenAI:ApiKey", "sk-test-placeholder-not-a-real-key-1234567890");
            builder.UseSetting(ModelEndpointPolicy.Setting, "true");
            if (withKey) builder.UseSetting("Secrets:MasterKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            TestSecurity.Configure(builder, legacyGeneration: false);
            builder.ConfigureServices(services => { services.RemoveAll<IModelRegistryStore>(); services.AddSingleton<IModelRegistryStore>(Durable.Api); });
        });
        object Body(string url) => new { name = "http-" + Guid.NewGuid().ToString("N")[..8], protocol = "OpenAiCompatible", baseUrl = url, model = "m", apiKey = CustomerKey };
        using var keyed = Factory(true); using var client = keyed.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestSecurity.Token(subject: f.Actor.Subject));
        using (var created = await client.PostAsJsonAsync($"/api/organizations/{f.Organization}/models", Body(server.BaseUrl)))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.DoesNotContain(CustomerKey, await created.Content.ReadAsStringAsync());
        }
        using (var list = await client.GetAsync($"/api/organizations/{f.Organization}/models"))
            Assert.DoesNotContain(CustomerKey, await list.Content.ReadAsStringAsync());
        foreach (var bad in new[] { "http://example.com/v1", "https://user:pw@example.com/v1", "file:///etc/passwd" })
            using (var rejected = await client.PostAsJsonAsync($"/api/organizations/{f.Organization}/models", Body(bad)))
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using (var foreign = await client.PostAsJsonAsync("/api/organizations/another-organization/models", Body(server.BaseUrl)))
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var keyless = Factory(false); using var unavailable = keyless.CreateClient();
        unavailable.DefaultRequestHeaders.Authorization = new("Bearer", TestSecurity.Token(subject: f.Actor.Subject));
        using (var noKey = await unavailable.PostAsJsonAsync($"/api/organizations/{f.Organization}/models", Body(server.BaseUrl)))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, noKey.StatusCode);
        Assert.Equal(0, server.Completions);
    }
}
