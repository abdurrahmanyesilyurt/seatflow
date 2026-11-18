using Microsoft.Extensions.Logging;

namespace SeatFlow.Messaging.Consuming;

/// <summary>
/// Handles one integration message. Handlers only change the scoped DbContext and stage outbox messages: the inbox
/// dispatcher calls <c>SaveChanges</c> once, so the state change, the outgoing messages and the "already processed"
/// marker commit atomically. Never call <c>SaveChanges</c> from a handler.
/// </summary>
public interface IMessageHandler<in TMessage> where TMessage : class
{
    Task HandleAsync(TMessage message, MessageContext context, CancellationToken cancellationToken);
}

public sealed class MessageContext
{
    private List<Func<Task>>? _afterCommit;

    public required Guid MessageId { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>1 on first delivery; the quorum queue's <c>x-delivery-count</c> + 1 afterwards.</summary>
    public long DeliveryAttempt { get; init; }

    /// <summary>Registers work that must only happen once the transaction is durable (e.g. releasing Redis locks).</summary>
    public void OnCommitted(Func<Task> action) => (_afterCommit ??= []).Add(action);

    internal async Task RunAfterCommitAsync(ILogger logger)
    {
        if (_afterCommit is null)
        {
            return;
        }

        foreach (var action in _afterCommit)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                // Post-commit work is best effort by design (e.g. a lock that would expire on its own anyway).
                logger.LogWarning(ex, "Post-commit action for message {MessageId} failed", MessageId);
            }
        }
    }
}
