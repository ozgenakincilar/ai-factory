namespace Factory.Core.Models;

public sealed record ModelResponse(
    string Content,
    string Model,
    ModelResponseStatus Status = ModelResponseStatus.Completed,
    ModelUsage? Usage = null);

public enum ModelResponseStatus
{
    Completed,
    Refused,
    Incomplete,
    Empty
}

// Provider-reported usage for one call. Model is the model the provider says served the call,
// which can be a dated snapshot of the configured name. Absent when the provider reports none.
public sealed record ModelUsage(string Provider, string Model, int InputTokens, int OutputTokens);
