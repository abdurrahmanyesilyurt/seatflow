using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SeatFlow.Messaging;

public static class DatabaseStartup
{
    /// <summary>
    /// <c>dotnet App.dll migrate</c> applies migrations and exits (used by the Kubernetes init container);
    /// <c>Database:MigrateOnStartup=true</c> migrates before serving (used by docker compose and tests).
    /// Returns <c>true</c> when the process should exit.
    /// </summary>
    public static async Task<bool> RunDatabaseCommandsAsync<TDbContext>(this IHost app, string[] args)
        where TDbContext : DbContext
    {
        var configuration = app.Services.GetRequiredService<IConfiguration>();

        if (args.Contains("migrate", StringComparer.OrdinalIgnoreCase))
        {
            await app.MigrateDatabaseAsync<TDbContext>();
            return true;
        }

        if (configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            await app.MigrateDatabaseAsync<TDbContext>();
        }

        return false;
    }

    public static async Task MigrateDatabaseAsync<TDbContext>(this IHost app, CancellationToken cancellationToken = default)
        where TDbContext : DbContext
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseStartup));

        // Generous on purpose: on a fresh cluster the database pod may still be pulling its image. Waiting here keeps
        // the init container from falling into Kubernetes' exponential CrashLoopBackOff.
        const int maxAttempts = 60;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // EF Core takes a database-level migration lock, so concurrent replicas cannot both apply a migration.
                await db.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Database for {DbContext} is up to date", typeof(TDbContext).Name);
                return;
            }
            catch (NpgsqlException ex) when (attempt < maxAttempts && ex.IsTransient)
            {
                logger.LogWarning(ex, "Database not reachable yet (attempt {Attempt}/{Max})", attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }
}
