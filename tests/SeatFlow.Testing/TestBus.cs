using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SeatFlow.Messaging;

namespace SeatFlow.Testing;

/// <summary>
/// Plays "the other services" in a test: publishes messages the way the outbox relay does (same exchange, routing key,
/// type and message-id) and records everything the service under test publishes.
/// </summary>
public sealed class TestBus : IAsyncDisposable
{
    private static readonly MessageTypeRegistry Registry = new();

    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly string _exchange;
    private readonly ConcurrentQueue<(string Type, string MessageId, string Body)> _received = new();

    private TestBus(IConnection connection, IChannel channel, string exchange)
    {
        _connection = connection;
        _channel = channel;
        _exchange = exchange;
    }

    public static async Task<TestBus> StartAsync(string amqpUri, string exchange)
    {
        var connection = await new ConnectionFactory { Uri = new Uri(amqpUri) }.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false);

        var bus = new TestBus(connection, channel, exchange);
        var queue = (await channel.QueueDeclareAsync("", durable: false, exclusive: true, autoDelete: true)).QueueName;
        await channel.QueueBindAsync(queue, exchange, "#");

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            bus._received.Enqueue((
                delivery.BasicProperties.Type ?? delivery.RoutingKey,
                delivery.BasicProperties.MessageId ?? "",
                Encoding.UTF8.GetString(delivery.Body.Span)));
            return Task.CompletedTask;
        };
        await channel.BasicConsumeAsync(queue, autoAck: true, consumer);
        return bus;
    }

    /// <summary>Waits until <paramref name="queue"/> exists and has a consumer, i.e. the app finished declaring its topology.</summary>
    public async Task WaitForConsumerAsync(string queue)
    {
        await Eventually.TrueAsync(async () =>
        {
            try
            {
                await using var probe = await _connection.CreateChannelAsync();
                var ok = await probe.QueueDeclarePassiveAsync(queue);
                return ok.ConsumerCount > 0;
            }
            catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(30), $"a consumer on queue '{queue}'");
    }

    public async Task<Guid> PublishAsync<T>(T message, Guid? messageId = null) where T : class
    {
        var descriptor = Registry.Get(typeof(T));
        var id = messageId ?? Guid.NewGuid();
        var properties = new BasicProperties
        {
            MessageId = id.ToString(),
            Type = descriptor.Name,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(message, MessagingJson.Options);
        await _channel.BasicPublishAsync(_exchange, descriptor.Name, mandatory: false, properties, body);
        return id;
    }

    public IReadOnlyList<T> Received<T>(Func<T, bool>? predicate = null) where T : class
    {
        var name = Registry.Get(typeof(T)).Name;
        return _received
            .Where(m => m.Type == name)
            .Select(m => JsonSerializer.Deserialize<T>(m.Body, MessagingJson.Options)!)
            .Where(m => predicate?.Invoke(m) ?? true)
            .ToList();
    }

    public Task<T> WaitForAsync<T>(Func<T, bool> predicate, TimeSpan? timeout = null) where T : class =>
        Eventually.GetAsync(
            () => Task.FromResult(Received(predicate).FirstOrDefault()),
            timeout,
            $"a {typeof(T).Name} message");

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _connection.CloseAsync(TimeSpan.FromSeconds(5));
        }
        catch (RabbitMQ.Client.Exceptions.AlreadyClosedException)
        {
        }

        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
