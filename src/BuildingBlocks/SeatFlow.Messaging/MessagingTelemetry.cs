using System.Diagnostics;
using System.Text;
using RabbitMQ.Client.Events;
using SeatFlow.Messaging.Outbox;

namespace SeatFlow.Messaging;

/// <summary>
/// W3C trace-context propagation across the broker. The outbox stores the <c>traceparent</c> of the request that
/// produced a message, so the publish span (which runs later, on the relay) still joins the original trace.
/// </summary>
public static class MessagingTelemetry
{
    public const string ActivitySourceName = "SeatFlow.Messaging";

    internal const string TraceParentHeader = "traceparent";
    internal const string TraceStateHeader = "tracestate";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    internal static Activity? StartPublish(OutboxMessage message, string exchange)
    {
        ActivityContext.TryParse(message.TraceParent, message.TraceState, out var parent);
        var activity = Source.StartActivity($"publish {message.MessageType}", ActivityKind.Producer, parent);
        activity?
            .SetTag("messaging.system", "rabbitmq")
            .SetTag("messaging.operation.type", "publish")
            .SetTag("messaging.destination.name", exchange)
            .SetTag("messaging.rabbitmq.destination.routing_key", message.MessageType)
            .SetTag("messaging.message.id", message.Id.ToString());
        return activity;
    }

    internal static Activity? StartProcess(BasicDeliverEventArgs delivery, string queue, string messageType)
    {
        var headers = delivery.BasicProperties.Headers;
        ActivityContext.TryParse(ReadHeader(headers, TraceParentHeader), ReadHeader(headers, TraceStateHeader), out var parent);
        var activity = Source.StartActivity($"process {messageType}", ActivityKind.Consumer, parent);
        activity?
            .SetTag("messaging.system", "rabbitmq")
            .SetTag("messaging.operation.type", "process")
            .SetTag("messaging.destination.name", queue)
            .SetTag("messaging.rabbitmq.destination.routing_key", delivery.RoutingKey)
            .SetTag("messaging.message.id", delivery.BasicProperties.MessageId);
        return activity;
    }

    internal static string? ReadHeader(IDictionary<string, object?>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => null,
            }
            : null;
}
