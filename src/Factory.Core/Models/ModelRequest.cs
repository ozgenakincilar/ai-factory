namespace Factory.Core.Models;

public sealed record ModelRequest(
    string SystemPrompt,
    string UserPrompt,
    ModelOutputSchema? OutputSchema = null);

public sealed record ModelOutputSchema(string Name, string JsonSchema);
