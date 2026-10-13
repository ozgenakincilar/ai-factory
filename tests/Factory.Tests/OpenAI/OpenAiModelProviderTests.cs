using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using Factory.Core.Agents;
using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Models;
using Factory.Infrastructure.OpenAI;
using Factory.Orchestration.Agents;
using Factory.Tests.Analysis;
using OpenAI;
using OpenAI.Chat;

namespace Factory.Tests.OpenAI;

public class OpenAiModelProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerateAsync_SendsStrictSchemaInActualSdkRequest(bool architect)
    {
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var outputSchema = architect ? ArchitectureJson.OutputSchema : AnalysisJson.OutputSchema;
        var result = await Provider(http).GenerateAsync(new("System", "User", outputSchema));
        Assert.Equal(ModelResponseStatus.Completed, result.Status);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        var root = request.RootElement;
        Assert.Equal("/v1/chat/completions", handler.RequestUri!.AbsolutePath);
        Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString());
        var format = root.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("json_schema");
        Assert.True(schema.GetProperty("strict").GetBoolean());
        Assert.Equal(outputSchema.Name, schema.GetProperty("name").GetString());
        using var expected = JsonDocument.Parse(outputSchema.JsonSchema);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, schema.GetProperty("schema")));
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task GenerateAsync_NoSchema_PreservesPlainTextProviderUsage()
    {
        using var handler = new StubHandler { Content = "Plain response" };
        using var http = new HttpClient(handler);
        var result = await Provider(http).GenerateAsync(new("System", "User"));
        Assert.Equal("Plain response", result.Content);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(request.RootElement.TryGetProperty("response_format", out _));
    }

    [Theory]
    [InlineData("stop", "Refused for safety", "{}", ModelResponseStatus.Refused)]
    [InlineData("content_filter", null, "partial", ModelResponseStatus.Refused)]
    [InlineData("length", null, "{}", ModelResponseStatus.Incomplete)]
    [InlineData("tool_calls", null, "{}", ModelResponseStatus.Incomplete)]
    [InlineData("stop", null, "", ModelResponseStatus.Empty)]
    [InlineData("stop", null, " ", ModelResponseStatus.Empty)]
    [InlineData("stop", null, null, ModelResponseStatus.Empty)]
    public async Task GenerateAsync_ClassifiesNonSuccessWithoutExposingContent(
        string finishReason, string? refusal, string? content, ModelResponseStatus expected)
    {
        using var handler = new StubHandler { FinishReason = finishReason, Refusal = refusal, Content = content };
        using var http = new HttpClient(handler);
        var result = await Provider(http).GenerateAsync(new("System", "User", AnalysisJson.OutputSchema));
        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_SdkApiErrorPropagatesToExistingApiHandler()
    {
        using var handler = new StubHandler { Status = HttpStatusCode.BadRequest };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ClientResultException>(() =>
            new AnalystAgent(Provider(http)).ExecuteAsync(new AgentRequest("project", "Analyze", "")));
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderAndAgentReturnValidatedTypedAnalysis()
    {
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var result = await new AnalystAgent(Provider(http)).ExecuteAsync(new("project", "Analyze", ""));
        Assert.True(result.Success);
        Assert.NotNull(result.Analysis);
        Assert.Equal(RequirementStatus.Suggested, Assert.Single(result.Analysis.FunctionalRequirements).Status);
    }

    private static OpenAiModelProvider Provider(HttpClient http) => new(
        new ChatClient("gpt-4o-mini", new ApiKeyCredential("synthetic-test-credential"), new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(http)
        }), "gpt-4o-mini");

    private sealed class StubHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string FinishReason { get; init; } = "stop";
        public string? Refusal { get; init; }
        public string? Content { get; init; } = AnalysisTests.SampleJson;
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = Status == HttpStatusCode.OK
                ? JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-test", @object = "chat.completion", created = 1, model = "gpt-4o-mini",
                    choices = new[] { new { index = 0, finish_reason = FinishReason,
                        message = new { role = "assistant", content = Content, refusal = Refusal } } }
                })
                : """{"error":{"message":"Synthetic API failure","type":"invalid_request_error","code":"invalid_request"}}""";
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
