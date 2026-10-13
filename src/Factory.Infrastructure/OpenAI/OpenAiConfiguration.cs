namespace Factory.Infrastructure.OpenAI;

public static class OpenAiConfiguration
{
    public static void ValidateApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OpenAI:ApiKey is not configured. Set it in User Secrets or the environment.");
        }

        // Validate without modifying the credential or including it in an exception.
        // Invisible characters in the Authorization header can cause an empty HTTP 400.
        if (apiKey.Any(c => c < '!' || c > '~') ||
            apiKey.Contains('"') || apiKey.Contains('\''))
        {
            throw new InvalidOperationException(
                "OpenAI:ApiKey contains whitespace, control characters, non-ASCII characters, " +
                "or quotes. Re-enter the original API key in User Secrets or the environment. " +
                "Use only the key value, without a Bearer prefix. Do not paste it into logs or chat.");
        }
    }
}
