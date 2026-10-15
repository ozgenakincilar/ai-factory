namespace Factory.Core.Models;

// MaxOutputTokens bounds generation; budget reservations price this bound as the worst case.
public sealed record ModelRequest(
    string SystemPrompt,
    string UserPrompt,
    ModelOutputSchema? OutputSchema = null,
    int? MaxOutputTokens = null);

public sealed record ModelOutputSchema(string Name, string JsonSchema);
