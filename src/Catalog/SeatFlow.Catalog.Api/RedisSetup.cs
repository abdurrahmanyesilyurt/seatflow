using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Trace;
using StackExchange.Redis;

namespace SeatFlow.Catalog;

internal static class RedisSetup
{
    /// <summary>
    /// HybridCache = in-process L1 + Redis L2, with stampede protection (one factory call per key under load)
    /// and tag-based invalidation when an event is published.
    /// </summary>
    public static void AddCatalogCache(this WebApplicationBuilder builder)
    {
        var redis = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379");
        redis.AbortOnConnectFail = false; // start even if Redis is down; the cache degrades to L1 only

        builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
        builder.Services.AddStackExchangeRedisCache(o => o.InstanceName = "catalog:");
        builder.Services.AddOptions<RedisCacheOptions>()
            .Configure<IConnectionMultiplexer>((o, mux) => o.ConnectionMultiplexerFactory = () => Task.FromResult(mux));

        builder.Services.AddHybridCache(o => o.DefaultEntryOptions = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(5),
            LocalCacheExpiration = TimeSpan.FromSeconds(30),
        });

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
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis unreachable", ex);
        }
    }
}
