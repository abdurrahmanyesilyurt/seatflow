using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SeatFlow.Messaging.Outbox;

namespace SeatFlow.Messaging.Consuming;

/// <summary>
/// Idempotent consumer. The inbox row (message id + handler) is inserted by the same <c>SaveChanges</c> as the
/// handler's changes. A redelivered message is skipped; two concurrent deliveries of the same message race on the
/// inbox primary key, one wins, the other fails, is requeued and then skipped.
/// </summary>
internal sealed partial class InboxDispatcher<TDbContext>(
    TDbContext db,
    TimeProvider time,
    ILogger<InboxDispatcher<TDbContext>> logger)
    where TDbContext : DbContext
{
    public async Task DispatchAsync<TMessage>(
        TMessage message,
        MessageContext context,
        IMessageHandler<TMessage> handler,
        CancellationToken cancellationToken)
        where TMessage : class
    {
        var consumer = handler.GetType().Name;

        var alreadyProcessed = await db.Set<InboxMessage>()
            .AnyAsync(m => m.MessageId == context.MessageId && m.Consumer == consumer, cancellationToken);
        if (alreadyProcessed)
        {
            LogDuplicate(typeof(TMessage).Name, context.MessageId);
            return;
        }

        await handler.HandleAsync(message, context, cancellationToken);

        db.Set<InboxMessage>().Add(new InboxMessage
        {
            MessageId = context.MessageId,
            Consumer = consumer,
            ProcessedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);

        await context.RunAfterCommitAsync(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping duplicate {MessageType} {MessageId}")]
    private partial void LogDuplicate(string messageType, Guid messageId);
}
