using Factory.Core.Models;
using OpenAI.Chat;

namespace Factory.Infrastructure.OpenAI;

public sealed class OpenAiModelProvider : IModelProvider
{
    private readonly ChatClient _client;
    private readonly string _model;

    public OpenAiModelProvider(
        ChatClient client,
        string model)
    {
        _client = client;
        _model = model;
    }

    public async Task<ModelResponse> GenerateAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> messages =
        [
            new SystemChatMessage(request.SystemPrompt),
            new UserChatMessage(request.UserPrompt)
        ];

        ChatCompletionOptions? options = request.OutputSchema is { } schema
            ? new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    schema.Name, BinaryData.FromString(schema.JsonSchema), jsonSchemaIsStrict: true)
            }
            : null;

        ChatCompletion completion =
            await _client.CompleteChatAsync(
                messages,
                options,
                cancellationToken: cancellationToken);

        // Every returned completion is billed, including refusals and partial output.
        var usage = completion.Usage is { } reported
            ? new ModelUsage("openai", string.IsNullOrWhiteSpace(completion.Model) ? _model : completion.Model,
                reported.InputTokenCount, reported.OutputTokenCount)
            : null;

        // Refusals and partial completions need not conform to the requested schema.
        if (!string.IsNullOrEmpty(completion.Refusal) || completion.FinishReason == ChatFinishReason.ContentFilter)
            return new ModelResponse(string.Empty, _model, ModelResponseStatus.Refused, usage);
        if (completion.FinishReason != ChatFinishReason.Stop)
            return new ModelResponse(string.Empty, _model, ModelResponseStatus.Incomplete, usage);

        string content = string.Join(
            "\n",
            completion.Content
                .Where(part => !string.IsNullOrWhiteSpace(part.Text))
                .Select(part => part.Text));

        if (string.IsNullOrWhiteSpace(content))
        {
            return new ModelResponse(string.Empty, _model, ModelResponseStatus.Empty, usage);
        }

        return new ModelResponse(content, _model, Usage: usage);
    }
}
