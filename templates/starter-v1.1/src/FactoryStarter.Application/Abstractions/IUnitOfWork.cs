namespace FactoryStarter.Application.Abstractions;

// Port implemented by the infrastructure layer: commits all changes of one use case atomically.
public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
