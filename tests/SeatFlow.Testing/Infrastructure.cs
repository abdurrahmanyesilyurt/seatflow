using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Xunit;

namespace SeatFlow.Testing;

/// <summary>
/// Real PostgreSQL, Redis and RabbitMQ in throwaway containers, started once per test assembly and in parallel.
/// The same image versions as docker-compose, so the tests exercise what production runs.
/// </summary>
public sealed class Infrastructure : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public RedisContainer Redis { get; } = new RedisBuilder("redis:8.8-alpine").Build();

    public RabbitMqContainer RabbitMq { get; } = new RabbitMqBuilder("rabbitmq:4.3-alpine").Build();

    /// <summary>A connection string to a database of its own; EF Core creates it on first migration.</summary>
    public string PostgresConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(Postgres.GetConnectionString()) { Database = database }.ConnectionString;

    public async ValueTask InitializeAsync() =>
        await Task.WhenAll(Postgres.StartAsync(), Redis.StartAsync(), RabbitMq.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await Postgres.DisposeAsync();
        await Redis.DisposeAsync();
        await RabbitMq.DisposeAsync();
    }
}

public static class Eventually
{
    /// <summary>Polls until <paramref name="probe"/> returns a value, or fails the test after the timeout.</summary>
    public static async Task<T> GetAsync<T>(Func<Task<T?>> probe, TimeSpan? timeout = null, string? because = null)
        where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            var value = await probe();
            if (value is not null)
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {because ?? "condition"}.");
            }

            await Task.Delay(100);
        }
    }

    public static async Task TrueAsync(Func<Task<bool>> probe, TimeSpan? timeout = null, string? because = null) =>
        await GetAsync<object>(async () => await probe() ? new object() : null, timeout, because);
}
