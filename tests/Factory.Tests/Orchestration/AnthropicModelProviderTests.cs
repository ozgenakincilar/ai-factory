using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core.Analysis;
using Factory.Core.Models;
using Factory.Core.Tenancy;
using Factory.Infrastructure.Anthropic;
using Factory.Infrastructure.OpenAI;
using Factory.Infrastructure.Security;

namespace Factory.Tests.Orchestration;

public class AnthropicModelProviderTests
{
    private const string Key = "sk-ant-test-placeholder-0123456789";

    private static AnthropicModelProvider Provider(FakeAnthropicServer server, string label = "anthropic")
        => new(AnthropicModelProvider.CreateClient(Key, new Uri(server.BaseUrl), new HttpClient()), "claude-haiku-5-5", label);

    [Fact]
    public async Task SendsTheSchemaBoundAndKeyAndReportsAllPromptTokensAsInput()
    {
        await using var server = await FakeAnthropicServer.StartAsync();
        server.ServedModel = "claude-haiku-5-5-snapshot";
        var response = await Provider(server).GenerateAsync(new ModelRequest("system text", "user text", AnalysisJson.OutputSchema, 1234));
        Assert.Equal(ModelResponseStatus.Completed, response.Status);
        Assert.NotNull(JsonSerializer.Deserialize<AnalysisResult>(response.Content, AnalysisJson.Options));
        // input 11 + cache creation 2 + cache read 3; the served model is recorded for pricing.
        Assert.Equal(new ModelUsage("anthropic", "claude-haiku-5-5-snapshot", 16, 7), response.Usage);
        Assert.Equal((Key, 1), (server.LastApiKey, server.Messages));
        Assert.False(string.IsNullOrEmpty(server.LastVersion));
        var body = server.LastBody!;
        Assert.Equal(("claude-haiku-5-5", 1234), (body["model"]!.GetValue<string>(), body["max_tokens"]!.GetValue<int>()));
        Assert.Equal("system text", body["system"]!.GetValue<string>());
        Assert.Equal("user text", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("json_schema", body["output_config"]!["format"]!["type"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AnalysisJson.OutputSchema.JsonSchema), body["output_config"]!["format"]!["schema"]));
    }

    [Theory]
    [InlineData("refusal", ModelResponseStatus.Refused)]
    [InlineData("max_tokens", ModelResponseStatus.Incomplete)]
    [InlineData("pause_turn", ModelResponseStatus.Incomplete)]
    public async Task NonFinalStopReasonsReturnNoContentButKeepBilledUsage(string stopReason, ModelResponseStatus expected)
    {
        await using var server = await FakeAnthropicServer.StartAsync();
        server.StopReason = stopReason;
        var response = await Provider(server, "customer:x").GenerateAsync(new ModelRequest("s", "u"));
        Assert.Equal((expected, ""), (response.Status, response.Content));
        Assert.Equal(new ModelUsage("customer:x", "claude-haiku-5-5", 16, 7), response.Usage);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(529)]
    [InlineData(429)]
    public async Task FailuresAreNeverRetriedByTheClient(int status)
    {
        await using var server = await FakeAnthropicServer.StartAsync();
        server.Status = status;
        await Assert.ThrowsAnyAsync<Exception>(() => Provider(server).GenerateAsync(new ModelRequest("s", "u")));
        Assert.Equal(1, server.Messages);
    }

    [Fact]
    public async Task CustomerAnthropicModelsAreVerifiedThroughTheEgressPolicy()
    {
        await using var server = await FakeAnthropicServer.StartAsync();
        var protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));
        var id = Guid.NewGuid();
        var endpoint = new ModelEndpoint(id, "org-a", ModelProtocol.Anthropic, server.BaseUrl, "claude-haiku-5-5",
            protector.Protect(Key, ModelEndpoint.SecretContext("org-a", id)));
        var verifier = new ModelVerifier(new CustomerModelProviderFactory(protector, new ModelEndpointPolicy(allowLoopback: true)));
        Assert.Equal((ModelVerificationStatus.Verified, (bool?)true), await verifier.VerifyAsync(endpoint, default));
        Assert.Equal((Key, ModelRegistryLimits.VerificationMaxOutputTokens), (server.LastApiKey, server.LastBody!["max_tokens"]!.GetValue<int>()));
        server.NoStructuredOutput = true;
        Assert.Equal((ModelVerificationStatus.Verified, (bool?)false), await verifier.VerifyAsync(endpoint, default));
        server.Status = 400; // reachable, but the schema or parameters are rejected
        Assert.Equal((ModelVerificationStatus.Verified, (bool?)false), await verifier.VerifyAsync(endpoint, default));
        server.Status = 401;
        Assert.Equal((ModelVerificationStatus.Failed, (bool?)null), await verifier.VerifyAsync(endpoint, default));
        // Without the development loopback allowance the socket check refuses the connection.
        var calls = server.Messages;
        var strict = new ModelVerifier(new CustomerModelProviderFactory(protector, new ModelEndpointPolicy(allowLoopback: false)));
        Assert.Equal((ModelVerificationStatus.Failed, (bool?)null), await strict.VerifyAsync(endpoint with { BaseUrl = server.BaseUrl.Replace("http://", "https://") }, default));
        Assert.Equal(calls, server.Messages);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("sk-ant-with space")]
    [InlineData("\"sk-ant-quoted\"")]
    public void InvalidKeysAreRejectedWithoutEchoingThem(string? key)
    {
        var error = Assert.Throws<InvalidOperationException>(() => AnthropicConfiguration.ValidateApiKey(key));
        if (!string.IsNullOrWhiteSpace(key)) Assert.DoesNotContain(key, error.Message);
        Assert.Throws<InvalidOperationException>(() => AnthropicModelProvider.CreateClient(""));
    }
}
