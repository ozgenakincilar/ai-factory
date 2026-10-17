using FactoryStarter.Application.Abstractions;
using FactoryStarter.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace FactoryStarter.Infrastructure.Persistence;

// Maps aggregates to PostgreSQL. Entity configurations live next to this class
// (IEntityTypeConfiguration) so the domain model stays free of persistence attributes.
// EF Core creates entities through a constructor whose parameter names match the mapped properties
// (case-insensitively) or through a private parameterless constructor; give aggregates one of these.
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IUnitOfWork
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        // Domain events are dispatched after saving, never stored: no aggregate maps DomainEvents.
        foreach (var entity in modelBuilder.Model.GetEntityTypes().ToList())
            if (IsAggregate(entity.ClrType)) modelBuilder.Entity(entity.ClrType).Ignore(nameof(AggregateRoot<int>.DomainEvents));
    }

    async Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken) => await SaveChangesAsync(cancellationToken);

    private static bool IsAggregate(Type? type)
    {
        for (; type is not null; type = type.BaseType)
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AggregateRoot<>)) return true;
        return false;
    }
}
