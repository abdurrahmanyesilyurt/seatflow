using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace SeatFlow.Messaging.Outbox;

/// <summary>
/// Moves committed outbox rows to RabbitMQ. Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>, so any number of
/// replicas can run the relay without publishing the same row twice concurrently. A row is marked processed only
/// after the broker confirms it (publisher confirms); a crash in between re-publishes it, which the consumers' inbox
/// absorbs. That is the at-least-once half of the contract.
/// </summary>
internal sealed partial class OutboxRelay<TDbContext>(
    IServiceScopeFactory scopeFactory,
    RabbitMqConnection connection,
    OutboxSignal signal,
    IOptions<MessagingOptions> options,
    IHostEnvironment environment,
    TimeProvider time,
    ILogger<OutboxRelay<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private readonly MessagingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IChannel? channel = null;
        var lastCleanup = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                channel ??= await OpenChannelAsync(stoppingToken);

                int claimed;
                do
                {
                    claimed = await PublishBatchAsync(channel, stoppingToken);
                }
                while (claimed == _options.OutboxBatchSize && !stoppingToken.IsCancellationRequested);

                if (time.GetUtcNow() - lastCleanup > CleanupInterval)
                {
                    await CleanupAsync(stoppingToken);
                    lastCleanup = time.GetUtcNow();
                }

                await signal.WaitAsync(_options.OutboxPollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogRelayFailed(ex);
                if (channel is { IsOpen: false })
                {
                    await channel.DisposeAsync();
                    channel = null;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        if (channel is not null)
        {
            try
            {
                await channel.DisposeAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or AlreadyClosedException)
            {
                // Shutting down anyway.
            }
        }
    }

    private async Task<IChannel> OpenChannelAsync(CancellationToken cancellationToken)
    {
        var amqp = await connection.GetAsync(cancellationToken);
        var channel = await amqp.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await RabbitMqTopology.DeclareExchangesAsync(channel, _options, cancellationToken);
        return channel;
    }

    private async Task<int> PublishBatchAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var now = time.GetUtcNow();
        var batch = await db.Set<OutboxMessage>()
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE processed_at IS NULL AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                ORDER BY occurred_at
                LIMIT {_options.OutboxBatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        foreach (var message in batch)
        {
            try
            {
                await PublishAsync(channel, message, cancellationToken);
                message.ProcessedAt = time.GetUtcNow();
                message.LastError = null;
            }
            catch (PublishException ex)
            {
                // Returned (no queue bound for a mandatory message) or nacked: back off and try again later.
                message.Attempts++;
                message.NextAttemptAt = now + Backoff(message.Attempts);
                message.LastError = ex.IsReturn
                    ? "Unroutable: no queue is bound to this routing key yet."
                    : "The broker did not confirm the message.";
                LogPublishDeferred(message.MessageType, message.Id, message.Attempts, message.LastError);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return batch.Count;
    }

    private async Task PublishAsync(IChannel channel, OutboxMessage message, CancellationToken cancellationToken)
    {
        using var activity = MessagingTelemetry.StartPublish(message, _options.Exchange);

        var headers = new Dictionary<string, object?>();
        var traceParent = activity?.Id ?? message.TraceParent;
        var traceState = activity?.TraceStateString ?? message.TraceState;
        if (traceParent is not null)
        {
            headers[MessagingTelemetry.TraceParentHeader] = traceParent;
        }

        if (traceState is not null)
        {
            headers[MessagingTelemetry.TraceStateHeader] = traceState;
        }

        var properties = new BasicProperties
        {
            MessageId = message.Id.ToString(),
            Type = message.MessageType,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
            CorrelationId = message.CorrelationId,
            AppId = environment.ApplicationName,
            Headers = headers,
        };

        await channel.BasicPublishAsync(
            _options.Exchange,
            message.MessageType,
            message.Mandatory,
            properties,
            Encoding.UTF8.GetBytes(message.Payload),
            cancellationToken);
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var cutoff = time.GetUtcNow() - _options.OutboxRetention;

        await db.Set<OutboxMessage>().Where(m => m.ProcessedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        await db.Set<InboxMessage>().Where(m => m.ProcessedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempts), 60));

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox relay iteration failed; retrying")]
    private partial void LogRelayFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deferred {MessageType} {MessageId} (attempt {Attempts}): {Reason}")]
    private partial void LogPublishDeferred(string messageType, Guid messageId, int attempts, string reason);
}
