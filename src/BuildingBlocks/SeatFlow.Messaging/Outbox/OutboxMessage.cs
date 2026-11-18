namespace SeatFlow.Messaging.Outbox;

/// <summary>A message written in the same database transaction as the state change that caused it.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; }

    /// <summary>Wire name, used as routing key and AMQP <c>type</c>.</summary>
    public required string MessageType { get; init; }

    public required string Payload { get; init; }

    public bool Mandatory { get; init; }

    public string? CorrelationId { get; init; }

    public string? TraceParent { get; init; }

    public string? TraceState { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

/// <summary>Remembers which message ids a handler has already applied, turning at-least-once delivery into effectively-once processing.</summary>
public sealed class InboxMessage
{
    public Guid MessageId { get; init; }

    public required string Consumer { get; init; }

    public DateTimeOffset ProcessedAt { get; init; }
}
