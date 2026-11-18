using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace SeatFlow.Messaging;

public static class PostgresConnection
{
    /// <summary>
    /// Reads <c>ConnectionStrings:{name}</c>. GSS encryption is turned off: the chiseled runtime image has no
    /// libgssapi, and Npgsql would otherwise probe for it on every new physical connection and log an error.
    /// </summary>
    public static string FromConfiguration(IConfiguration configuration, string name = "Postgres")
    {
        var connectionString = configuration.GetConnectionString(name)
            ?? throw new InvalidOperationException($"ConnectionStrings:{name} is not configured.");
        return new NpgsqlConnectionStringBuilder(connectionString) { GssEncryptionMode = GssEncryptionMode.Disable }.ConnectionString;
    }

    /// <summary>
    /// PostgreSQL via Npgsql, with SQL spans only inside an operation (an HTTP request, a consumed message, a publish).
    /// Without the filter the outbox relay's once-a-second poll would flood the tracing backend with root spans.
    /// </summary>
    public static DbContextOptionsBuilder UseSeatFlowPostgres(this DbContextOptionsBuilder options, IConfiguration configuration) =>
        options.UseNpgsql(
            FromConfiguration(configuration),
            npgsql => npgsql.ConfigureDataSource(dataSource =>
                dataSource.ConfigureTracing(tracing => tracing.ConfigureCommandFilter(_ => Activity.Current is not null))));
}
