namespace FactoryStarter.Application.Abstractions;

// Port implemented by the infrastructure layer: whether the application's database answers.
public interface IDatabaseHealth
{
    Task<bool> CanConnectAsync(CancellationToken cancellationToken = default);
}
