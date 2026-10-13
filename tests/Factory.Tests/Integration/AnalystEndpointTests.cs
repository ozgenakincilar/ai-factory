using System.ClientModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Core.Agents;
using Factory.Core.Analysis;
using Factory.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Factory.Tests.Integration;

public class AnalystEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private static readonly AgentRequest Request = new("integration", "Add tasks and mark them complete", "Single user");
    private static readonly AnalysisResult Analysis = new("Proposed task list",
        [new("FR-1", "Add tasks", RequirementStatus.Suggested, null)], [], [], []);

    [Fact]
    public async Task Post_ValidOutput_ReturnsTypedAnalysisAndStringEnums()
    {
        using var factory = new ApiFactory(new StubProvider());
        using var client = TestSecurity.Client(factory);
        using var response = await client.PostAsJsonAsync("/api/agents/analyst", Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var result = await response.Content.ReadFromJsonAsync<AgentResult>(JsonOptions);
        Assert.True(result!.Success);
        Assert.True(AnalysisValidator.IsValid(result.Analysis, null));
        Assert.Equal(RequirementStatus.Suggested, Assert.Single(result.Analysis!.FunctionalRequirements).Status);
        Assert.Equal(JsonSerializer.Serialize(result.Analysis, AnalysisJson.Options), result.Output);
    }

    [Theory]
    [InlineData("refused", 422, "refused")]
    [InlineData("incomplete", 502, "incompleteResponse")]
    [InlineData("empty", 502, "emptyResponse")]
    [InlineData("malformed", 502, "invalidOutput")]
    [InlineData("unsupportedConfirmation", 502, "invalidOutput")]
    public async Task Post_UnusableModelOutput_ReturnsSafeFailure(string scenario, int status, string failureKind)
    {
        using var factory = new ApiFactory(new StubProvider(scenario));
        using var client = TestSecurity.Client(factory);
        using var response = await client.PostAsJsonAsync("/api/agents/analyst", Request);
        Assert.Equal(status, (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(failureKind, json.RootElement.GetProperty("failureKind").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("analysis").ValueKind);
        Assert.Equal("", json.RootElement.GetProperty("output").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_InvalidInput_Returns400WithoutCallingProvider(bool duplicateSources)
    {
        var provider = new StubProvider();
        using var factory = new ApiFactory(provider);
        using var client = TestSecurity.Client(factory);
        var request = duplicateSources
            ? Request with { ConfirmedRequirements = [new("same", "A"), new("same", "B")] }
            : Request with { Task = "" };
        using var response = await client.PostAsJsonAsync("/api/agents/analyst", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Post_ExplicitConfirmation_IsBoundAndValidated()
    {
        using var factory = new ApiFactory(new StubProvider("unsupportedConfirmation"));
        using var client = TestSecurity.Client(factory);
        using var response = await client.PostAsJsonAsync("/api/agents/analyst", Request with
        {
            ConfirmedRequirements = [new("USER-1", "Add tasks")]
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AgentResult>(JsonOptions);
        Assert.Equal(RequirementStatus.Confirmed, Assert.Single(result!.Analysis!.FunctionalRequirements).Status);
    }

    [Fact]
    public async Task Post_ApiException_Returns502WithoutUpstreamMessage()
    {
        using var factory = new ApiFactory(new StubProvider("apiError"));
        using var client = TestSecurity.Client(factory);
        using var response = await client.PostAsJsonAsync("/api/agents/analyst", Request);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private upstream detail", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(502, json.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public void CapturedLiveResponse_DeserializesAndPreservesUnconfirmedProvenance()
    {
        // Offline validation of the saved real response, not a new live API call.
        var request = JsonSerializer.Deserialize<AgentRequest>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Evidence", "analyst-live-request.json")), JsonOptions)!;
        var result = JsonSerializer.Deserialize<AgentResult>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Evidence", "analyst-live-response.json")), JsonOptions)!;
        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Null(result.FailureKind);
        Assert.True(AnalysisValidator.IsValid(result.Analysis, request.ConfirmedRequirements));
        Assert.NotEmpty(result.Analysis!.FunctionalRequirements);
        Assert.All(result.Analysis.FunctionalRequirements, requirement =>
        {
            Assert.Contains(requirement.Status, new[] { RequirementStatus.Suggested, RequirementStatus.UnverifiedAssumption });
            Assert.Null(requirement.ConfirmedSourceId);
        });
        Assert.Equal(JsonSerializer.Serialize(result.Analysis, AnalysisJson.Options), result.Output);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(AnalysisJson.Options);
        options.Converters.Add(new JsonStringEnumConverter<AgentFailureKind>(JsonNamingPolicy.CamelCase, false));
        return options;
    }

    private sealed class ApiFactory(StubProvider provider) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Tests do not read Development secrets or contact OpenAI.
            builder.UseEnvironment("Testing");
            TestSecurity.Configure(builder);
            // Avoid machine-wide Windows Event Log writes from sandboxed tests.
            builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
            builder.UseSetting("OpenAI:ApiKey", "synthetic-test-credential");
            builder.UseSetting("OpenAI:Model", "gpt-4o-mini");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IModelProvider>();
                services.AddSingleton<IModelProvider>(provider);
            });
        }
    }

    private sealed class StubProvider(string scenario = "success") : IModelProvider
    {
        public int Calls { get; private set; }
        public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Equal(AnalysisJson.OutputSchema, request.OutputSchema);
            if (scenario == "apiError") throw new ClientResultException("private upstream detail");
            var analysis = scenario == "unsupportedConfirmation"
                ? Analysis with { FunctionalRequirements = [new("FR-1", "Add tasks", RequirementStatus.Confirmed, "USER-1")] }
                : Analysis;
            var status = scenario switch
            {
                "refused" => ModelResponseStatus.Refused,
                "incomplete" => ModelResponseStatus.Incomplete,
                "empty" => ModelResponseStatus.Empty,
                _ => ModelResponseStatus.Completed
            };
            return Task.FromResult(new ModelResponse(
                scenario == "malformed" ? "private non-JSON model text" : JsonSerializer.Serialize(analysis, AnalysisJson.Options),
                "synthetic-model", status));
        }
    }
}
