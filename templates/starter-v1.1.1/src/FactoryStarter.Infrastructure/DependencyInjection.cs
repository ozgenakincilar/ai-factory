using FactoryStarter.Application.Abstractions;
using FactoryStarter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryStarter.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("database_connection_not_configured");
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connection));
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<AppDbContext>());
        services.AddScoped<IDatabaseHealth, DatabaseHealth>();
        return services;
    }

    // Creates the tables of the current model when the database has none (staging deployments opt in
    // with Database:InitializeSchema). It never changes an existing schema; versioned migrations are
    // needed before a model change reaches a database that already has tables.
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = services.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is Npgsql.NpgsqlException or TimeoutException)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken);
            }
        }
    }
}
