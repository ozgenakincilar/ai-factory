using FactoryStarter.Application.Abstractions;
using FactoryStarter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

    // Creates the tables of the current model when they do not exist yet (staging deployments opt in
    // with Database:InitializeSchema), also in a database that holds other tables, for example those of
    // an earlier release. It never changes an existing table: when the model's first table already
    // exists the schema is kept as it is, so versioned migrations are needed before a model change
    // reaches a database whose tables already exist.
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database;
            try
            {
                var creator = database.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
                if (!await creator.ExistsAsync(cancellationToken)) await creator.CreateAsync(cancellationToken);
                await creator.CreateTablesAsync(cancellationToken);
                return;
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.DuplicateTable)
            {
                return; // The model's tables exist already.
            }
            catch (Exception ex) when (attempt < 5 && ex is Npgsql.NpgsqlException or TimeoutException)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken);
            }
        }
    }
}
