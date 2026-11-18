using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace SeatFlow.Messaging;

/// <summary>
/// One long-lived AMQP connection per process (channels are cheap, connections are not). The client's automatic
/// recovery reconnects and re-declares topology after a broker restart; the first connect retries until the broker is up,
/// so container start order does not matter.
/// </summary>
public sealed class RabbitMqConnection(
    IOptions<MessagingOptions> options,
    IHostEnvironment environment,
    ILogger<RabbitMqConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public bool IsOpen => _connection?.IsOpen == true;

    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            var factory = new ConnectionFactory
            {
                Uri = new Uri(options.Value.ConnectionString),
                ClientProvidedName = environment.ApplicationName,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            };

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    _connection = await factory.CreateConnectionAsync(cancellationToken);
                    logger.LogInformation("Connected to RabbitMQ at {Host}", factory.HostName);
                    return _connection;
                }
                catch (BrokerUnreachableException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(attempt * 2, 15));
                    logger.LogWarning(ex, "RabbitMQ is not reachable yet (attempt {Attempt}); retrying in {Delay}", attempt, delay);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            try
            {
                if (_connection.IsOpen)
                {
                    await _connection.CloseAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex) when (ex is AlreadyClosedException or ObjectDisposedException)
            {
                // Broker went away first.
            }

            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}

internal sealed class RabbitMqHealthCheck(RabbitMqConnection connection) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(connection.IsOpen
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus, "RabbitMQ connection is not open."));
}
