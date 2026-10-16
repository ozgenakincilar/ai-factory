using FactoryStarter.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FactoryStarter.Infrastructure.Persistence;

// Maps aggregates to PostgreSQL. Entity configurations live next to this class
// (IEntityTypeConfiguration) so the domain model stays free of persistence attributes.
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IUnitOfWork
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

    async Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken) => await SaveChangesAsync(cancellationToken);
}
