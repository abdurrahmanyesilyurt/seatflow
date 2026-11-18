using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SeatFlow.Messaging.Outbox;

public interface IOutbox
{
    /// <summary>
    /// Stages a message in the current DbContext. Nothing is sent until <c>SaveChanges</c> commits, and then the
    /// state change and the message are committed together or not at all.
    /// </summary>
    void Enqueue<TMessage>(TMessage message, string? correlationId = null) where TMessage : class;
}

internal sealed class EfOutbox<TDbContext>(TDbContext db, MessageTypeRegistry registry, TimeProvider time) : IOutbox
    where TDbContext : DbContext
{
    public void Enqueue<TMessage>(TMessage message, string? correlationId = null) where TMessage : class
    {
        // Runtime type, not TMessage: callers may pass a message typed as object (e.g. "reply for this state").
        var type = message.GetType();
        var descriptor = registry.Get(type);
        var activity = Activity.Current;

        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            MessageType = descriptor.Name,
            Mandatory = descriptor.Required,
            Payload = JsonSerializer.Serialize(message, type, MessagingJson.Options),
            CorrelationId = correlationId,
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString,
            OccurredAt = time.GetUtcNow(),
        });
    }
}

/// <summary>Wakes the relay right after a commit so messages leave in milliseconds instead of on the next poll.</summary>
public sealed class OutboxSignal : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        try
        {
            if (_semaphore.CurrentCount == 0)
            {
                _semaphore.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Another thread signalled first; one wake-up is enough.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _semaphore.Dispose();
}

public sealed class OutboxSignalInterceptor(OutboxSignal signal) : DbTransactionInterceptor, ISaveChangesInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => signal.Notify();

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        signal.Notify();
        return Task.CompletedTask;
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        NotifyIfNoTransaction(eventData);
        return result;
    }

    public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        NotifyIfNoTransaction(eventData);
        return ValueTask.FromResult(result);
    }

    private void NotifyIfNoTransaction(SaveChangesCompletedEventData eventData)
    {
        if (eventData.Context?.Database.CurrentTransaction is null)
        {
            signal.Notify();
        }
    }
}
