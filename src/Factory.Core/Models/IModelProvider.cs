namespace Factory.Core.Models;

public interface IModelProvider
{
    Task<ModelResponse> GenerateAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}