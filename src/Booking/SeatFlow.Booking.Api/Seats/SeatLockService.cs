using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Trace;
using StackExchange.Redis;

namespace SeatFlow.Bookings.Seats;

public enum SeatLockOutcome
{
    Acquired,
    Conflict,
    RedisUnavailable,
}

public sealed record SeatLockResult(SeatLockOutcome Outcome, IReadOnlyList<string> ConflictingSeats)
{
    public static readonly SeatLockResult Acquired = new(SeatLockOutcome.Acquired, []);
    public static readonly SeatLockResult Unavailable = new(SeatLockOutcome.RedisUnavailable, []);
}

/// <summary>
/// The fast path of the seat race. One Lua script checks every requested seat and, only if none is held by someone
/// else, sets all of them with a TTL: all-or-nothing, atomic on the Redis server, so two users asking for overlapping
/// seats can never each get half. The lock value is the booking id, which makes the script re-entrant for retries of the
/// same request and lets only the owner release or extend it. Keys share a {eventId} hash tag so the multi-key script
/// stays valid on Redis Cluster.
/// <para>Redis is an optimisation, not the source of truth: if it is down, holds still work and PostgreSQL's partial
/// unique index decides the race.</para>
/// </summary>
public sealed partial class SeatLockService(IConnectionMultiplexer redis, ILogger<SeatLockService> logger)
{
    private const string AcquireScript = """
        local conflicts = {}
        for i, key in ipairs(KEYS) do
          local owner = redis.call('GET', key)
          if owner and owner ~= ARGV[1] then
            table.insert(conflicts, i)
          end
        end
        if #conflicts > 0 then
          return conflicts
        end
        for _, key in ipairs(KEYS) do
          redis.call('SET', key, ARGV[1], 'PX', ARGV[2])
        end
        return conflicts
        """;

    private const string ReleaseScript = """
        local released = 0
        for _, key in ipairs(KEYS) do
          if redis.call('GET', key) == ARGV[1] then
            redis.call('DEL', key)
            released = released + 1
          end
        end
        return released
        """;

    public static RedisKey Key(Guid eventId, string seatId) => $"seat-lock:{{{eventId}}}:{seatId}";

    /// <summary>Acquires (or, for the same owner, refreshes the TTL of) every seat lock, or none of them.</summary>
    public async Task<SeatLockResult> TryAcquireAsync(Guid eventId, IReadOnlyList<string> seatIds, Guid owner, TimeSpan ttl)
    {
        var keys = seatIds.Select(s => Key(eventId, s)).ToArray();
        try
        {
            var result = await redis.GetDatabase().ScriptEvaluateAsync(
                AcquireScript, keys, [owner.ToString(), (long)ttl.TotalMilliseconds]);
            var conflicts = (int[]?)result ?? [];
            return conflicts.Length == 0
                ? SeatLockResult.Acquired
                : new SeatLockResult(SeatLockOutcome.Conflict, conflicts.Select(i => seatIds[i - 1]).ToList());
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            LogRedisUnavailable(ex);
            return SeatLockResult.Unavailable;
        }
    }

    /// <summary>Compare-and-delete: only removes locks this owner still holds. Best effort; TTLs clean up anyway.</summary>
    public async Task ReleaseAsync(Guid eventId, IEnumerable<string> seatIds, Guid owner)
    {
        var keys = seatIds.Select(s => Key(eventId, s)).ToArray();
        try
        {
            await redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, keys, [owner.ToString()]);
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            LogRedisUnavailable(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis unavailable; seat holds fall back to the database constraint")]
    private partial void LogRedisUnavailable(Exception exception);
}

internal static class SeatLockSetup
{
    public static void AddSeatLocks(this WebApplicationBuilder builder)
    {
        var redis = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379");
        redis.AbortOnConnectFail = false;
        redis.ConnectTimeout = 2_000;
        redis.SyncTimeout = 2_000;
        redis.AsyncTimeout = 2_000;

        builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
        builder.Services.AddSingleton<SeatLockService>();
        builder.Services.ConfigureOpenTelemetryTracerProvider(t => t.AddRedisInstrumentation());
        builder.Services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis", HealthStatus.Degraded, ["ready"]);
    }
}

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"ping {latency.TotalMilliseconds:F1} ms");
        }
        catch (RedisException ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis unreachable; running on database locks only", ex);
        }
    }
}
