namespace Factory.Infrastructure.Anthropic;

public static class AnthropicConfiguration
{
    // Demo default: the lowest-priced current Claude model, chosen for the USD 2 demo limit.
    public const string DefaultModel = "claude-haiku-5-5";

    public static void ValidateApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Anthropic:ApiKey is not configured. Set it in User Secrets or the environment.");

        // Validate without modifying the credential or including it in an exception.
        if (apiKey.Any(c => c < '!' || c > '~') || apiKey.Contains('"') || apiKey.Contains('\''))
            throw new InvalidOperationException(
                "Anthropic:ApiKey contains whitespace, control characters, non-ASCII characters, or quotes. " +
                "Re-enter the original API key in User Secrets or the environment. Do not paste it into logs or chat.");
    }
}
