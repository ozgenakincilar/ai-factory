using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Factory.Core.Models;

namespace Factory.Infrastructure.Anthropic;

// Claude Messages API adapter (official Anthropic SDK). Structured output uses output_config.format
// with the agent's JSON schema. providerLabel names the usage owner: "anthropic" for the Factory
// account, "customer:<id>" for a customer-registered Anthropic credential.
public sealed class AnthropicModelProvider : IModelProvider
{
    // Used only when a caller sets no bound; durable runs always pass the reserved bound.
    public const int DefaultMaxOutputTokens = 16_000;

    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly string _provider;

    public AnthropicModelProvider(AnthropicClient client, string model, string providerLabel = "anthropic")
    {
        _client = client;
        _model = model;
        _provider = providerLabel;
    }

    // The key is always explicit, so the SDK never falls back to environment or profile credentials.
    // No SDK retries: a lost or failed call is recorded, never silently repeated by the client.
    public static AnthropicClient CreateClient(string apiKey, Uri? baseUrl = null, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("anthropic_api_key_missing");
        // The SDK's own timeout governs; the HttpClient timeout is disabled so it cannot pre-empt it.
        return new AnthropicClient
        {
            ApiKey = apiKey,
            MaxRetries = 0,
            Timeout = TimeSpan.FromSeconds(90),
            BaseUrl = baseUrl?.ToString().TrimEnd('/') ?? global::Anthropic.Core.EnvironmentUrl.Production,
            HttpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
        };
    }

    public async Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = request.MaxOutputTokens ?? DefaultMaxOutputTokens,
            System = request.SystemPrompt,
            Messages = [new() { Role = Role.User, Content = request.UserPrompt }]
        };
        if (request.OutputSchema is { } schema)
            parameters = parameters with
            {
                OutputConfig = new OutputConfig
                {
                    Format = new JsonOutputFormat
                    {
                        Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schema.JsonSchema)
                            ?? throw new InvalidOperationException("invalid_output_schema")
                    }
                }
            };

        var message = await _client.Messages.Create(parameters, cancellationToken);

        // Every returned message is billed, including refusals and truncated output. All prompt
        // tokens (including cache reads/writes) are counted as input; this Factory sends no cache
        // breakpoints, so the cache counters are normally zero.
        var reported = message.Usage;
        var input = reported.InputTokens + (reported.CacheCreationInputTokens ?? 0) + (reported.CacheReadInputTokens ?? 0);
        // Raw(): the wire string; ToString() would yield the JSON form, quotes included.
        var servedModel = message.Model.Raw();
        var usage = new ModelUsage(_provider, string.IsNullOrWhiteSpace(servedModel) ? _model : servedModel,
            checked((int)input), checked((int)reported.OutputTokens));

        var stopReason = message.StopReason?.Raw();
        if (stopReason == "refusal")
            return new ModelResponse(string.Empty, _model, ModelResponseStatus.Refused, usage);
        if (stopReason != "end_turn")
            return new ModelResponse(string.Empty, _model, ModelResponseStatus.Incomplete, usage);

        var content = string.Join("\n", message.Content
            .Select(block => block.Value).OfType<TextBlock>()
            .Where(text => !string.IsNullOrWhiteSpace(text.Text))
            .Select(text => text.Text));
        return string.IsNullOrWhiteSpace(content)
            ? new ModelResponse(string.Empty, _model, ModelResponseStatus.Empty, usage)
            : new ModelResponse(content, _model, Usage: usage);
    }
}
