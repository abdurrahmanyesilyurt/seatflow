using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace SeatFlow.Messaging.Consuming;

internal sealed record MessageSubscription(
    MessageDescriptor Descriptor,
    Func<IServiceProvider, object, MessageContext, CancellationToken, Task> Invoke);

internal sealed class SubscriptionRegistry
{
    public Dictionary<string, MessageSubscription> Subscriptions { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Consumes this service's queue. Success acks; a handler exception nacks with requeue after a short backoff, and the
/// quorum queue's delivery limit dead-letters poison messages; malformed messages go straight to the dead-letter queue.
/// </summary>
internal sealed partial class RabbitMqConsumer(
    IServiceScopeFactory scopeFactory,
    RabbitMqConnection connection,
    SubscriptionRegistry registry,
    IOptions<MessagingOptions> options,
    ILogger<RabbitMqConsumer> logger) : BackgroundService
{
    private readonly MessagingOptions _options = options.Value;
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (registry.Subscriptions.Count == 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartConsumingAsync(stoppingToken);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogStartFailed(ex, _options.QueueName);
                if (_channel is not null)
                {
                    await _channel.DisposeAsync();
                    _channel = null;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task StartConsumingAsync(CancellationToken stoppingToken)
    {
        var amqp = await connection.GetAsync(stoppingToken);
        _channel = await amqp.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: _options.ConsumerConcurrency),
            stoppingToken);

        await RabbitMqTopology.DeclareExchangesAsync(_channel, _options, stoppingToken);
        await RabbitMqTopology.DeclareQueueAsync(_channel, _options, registry.Subscriptions.Keys, stoppingToken);
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: _options.PrefetchCount, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, delivery) => OnReceivedAsync(_channel, delivery, stoppingToken);
        await _channel.BasicConsumeAsync(_options.QueueName, autoAck: false, consumer, stoppingToken);

        LogConsuming(_options.QueueName, string.Join(", ", registry.Subscriptions.Keys));
    }

    private async Task OnReceivedAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        var properties = delivery.BasicProperties;
        var messageType = properties.Type ?? delivery.RoutingKey;

        if (!registry.Subscriptions.TryGetValue(messageType, out var subscription))
        {
            LogUnknownMessage(messageType);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            return;
        }

        using var activity = MessagingTelemetry.StartProcess(delivery, _options.QueueName, messageType);
        var attempt = ReadDeliveryCount(properties.Headers) + 1;

        try
        {
            if (!Guid.TryParse(properties.MessageId, out var messageId))
            {
                throw new JsonException("Message has no valid message-id; it cannot be processed idempotently.");
            }

            var message = JsonSerializer.Deserialize(delivery.Body.Span, subscription.Descriptor.Type, MessagingJson.Options)
                ?? throw new JsonException("Message body was empty.");

            var context = new MessageContext
            {
                MessageId = messageId,
                CorrelationId = properties.CorrelationId,
                DeliveryAttempt = attempt,
            };

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                await subscription.Invoke(scope.ServiceProvider, message, context, stoppingToken);
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
        }
        catch (JsonException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            LogPoisonMessage(ex, messageType, properties.MessageId);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            LogHandlerFailed(ex, messageType, properties.MessageId, attempt);

            // Small backoff so a transient failure (e.g. optimistic-concurrency conflict) is not retried in a hot loop.
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(200 * attempt * attempt, 5_000)), stoppingToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
        }
    }

    private static long ReadDeliveryCount(IDictionary<string, object?>? headers) =>
        headers is not null && headers.TryGetValue("x-delivery-count", out var value)
            ? value switch
            {
                long l => l,
                int i => i,
                _ => 0,
            }
            : 0;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is null)
        {
            return;
        }

        try
        {
            // DisposeAsync closes the channel; an explicit CloseAsync on an auto-recovering channel can race the
            // connection's own shutdown and throw ObjectDisposedException (RabbitMQ.Client 7.2).
            await _channel.DisposeAsync();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or RabbitMQ.Client.Exceptions.AlreadyClosedException or OperationCanceledException)
        {
            // Shutting down anyway.
        }

        _channel = null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming queue {Queue} for: {RoutingKeys}")]
    private partial void LogConsuming(string queue, string routingKeys);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not start consuming {Queue}; retrying")]
    private partial void LogStartFailed(Exception exception, string queue);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No handler registered for {MessageType}; acking and dropping it")]
    private partial void LogUnknownMessage(string messageType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Malformed {MessageType} {MessageId}; dead-lettering it")]
    private partial void LogPoisonMessage(Exception exception, string messageType, string? messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handling {MessageType} {MessageId} failed on attempt {Attempt}; requeueing")]
    private partial void LogHandlerFailed(Exception exception, string messageType, string? messageId, long attempt);
}
