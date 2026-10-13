namespace Factory.Core.Models;

public sealed record ModelResponse(
    string Content,
    string Model,
    ModelResponseStatus Status = ModelResponseStatus.Completed);

public enum ModelResponseStatus
{
    Completed,
    Refused,
    Incomplete,
    Empty
}
