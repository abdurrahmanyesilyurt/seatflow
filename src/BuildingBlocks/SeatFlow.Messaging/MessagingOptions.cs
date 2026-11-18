namespace SeatFlow.Messaging;

public sealed class MessagingOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>AMQP URI. Read from <c>ConnectionStrings:RabbitMq</c> when present.</summary>
    public string ConnectionString { get; set; } = "amqp://guest:guest@localhost:5672";

    /// <summary>Topic exchange every message is published to; the routing key is the message name.</summary>
    public string Exchange { get; set; } = "seatflow";

    public string DeadLetterExchange { get; set; } = "seatflow.dlx";

    /// <summary>The queue this service consumes from (one queue per service, competing consumers across replicas).</summary>
    public string QueueName { get; set; } = "";

    public ushort PrefetchCount { get; set; } = 16;

    public ushort ConsumerConcurrency { get; set; } = 4;

    /// <summary>Quorum-queue redelivery limit; after that the broker dead-letters the message.</summary>
    public int DeliveryLimit { get; set; } = 5;

    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int OutboxBatchSize { get; set; } = 50;

    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromDays(7);
}
