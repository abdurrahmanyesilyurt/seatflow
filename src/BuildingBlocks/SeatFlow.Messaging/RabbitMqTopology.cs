using RabbitMQ.Client;

namespace SeatFlow.Messaging;

/// <summary>
/// seatflow (topic) ──routing key──▶ {service} (quorum queue) ──after N failed deliveries──▶ seatflow.dlx ──▶ {service}.dead-letter
/// </summary>
internal static class RabbitMqTopology
{
    public static async Task DeclareExchangesAsync(IChannel channel, MessagingOptions options, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(options.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
    }

    public static async Task DeclareQueueAsync(
        IChannel channel,
        MessagingOptions options,
        IEnumerable<string> routingKeys,
        CancellationToken cancellationToken)
    {
        var queue = options.QueueName;
        var deadLetterQueue = $"{queue}.dead-letter";

        await channel.QueueDeclareAsync(
            deadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(deadLetterQueue, options.DeadLetterExchange, queue, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = options.DeliveryLimit,
                ["x-dead-letter-exchange"] = options.DeadLetterExchange,
                ["x-dead-letter-routing-key"] = queue,
            },
            cancellationToken: cancellationToken);

        foreach (var routingKey in routingKeys)
        {
            await channel.QueueBindAsync(queue, options.Exchange, routingKey, cancellationToken: cancellationToken);
        }
    }
}
