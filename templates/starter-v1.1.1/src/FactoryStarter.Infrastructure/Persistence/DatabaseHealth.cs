using FactoryStarter.Application.Abstractions;

namespace FactoryStarter.Infrastructure.Persistence;

internal sealed class DatabaseHealth(AppDbContext context) : IDatabaseHealth
{
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) => context.Database.CanConnectAsync(cancellationToken);
}
