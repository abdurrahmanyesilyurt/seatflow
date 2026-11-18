namespace SeatFlow.Contracts;

/// <summary>
/// Names an integration message on the wire. The name doubles as the RabbitMQ routing key and the
/// AMQP <c>type</c> property, so a consumer written in any language (the notification service is Python)
/// can route and deserialize without knowing about .NET types.
/// </summary>
/// <param name="name">Routing key, e.g. <c>booking.confirmed</c>.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MessageAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>
    /// Required messages are published with the AMQP <c>mandatory</c> flag. If no queue is bound yet
    /// (the consumer has never started), the broker returns the message and the outbox retries it later
    /// instead of silently dropping it. Commands and anything another service depends on are required;
    /// pure notifications are not.
    /// </summary>
    public bool Required { get; init; }
}
