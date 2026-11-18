using Microsoft.EntityFrameworkCore;
using SeatFlow.Bookings.Data;
using SeatFlow.Bookings.Domain;
using SeatFlow.Bookings.Seats;
using SeatFlow.Contracts;
using SeatFlow.Messaging.Consuming;
using SeatFlow.Messaging.Outbox;

namespace SeatFlow.Bookings.Messaging;

/// <summary>Keeps the local event/seat read model in sync with Catalog (upsert, so redelivery is harmless).</summary>
internal sealed class EventPublishedHandler(BookingDbContext db, TimeProvider time) : IMessageHandler<EventPublished>
{
    public async Task HandleAsync(EventPublished message, MessageContext context, CancellationToken cancellationToken)
    {
        var evt = await db.Events.FindAsync([message.EventId], cancellationToken);
        if (evt is null)
        {
            evt = new EventInfo { Id = message.EventId };
            db.Events.Add(evt);
        }

        evt.Title = message.Title;
        evt.VenueName = message.VenueName;
        evt.City = message.City;
        evt.StartsAt = message.StartsAt;
        evt.Currency = message.Currency;
        evt.UpdatedAt = time.GetUtcNow();

        var existing = await db.EventSeats
            .Where(s => s.EventId == message.EventId)
            .ToDictionaryAsync(s => s.SeatId, cancellationToken);

        foreach (var seat in message.Seats)
        {
            if (existing.TryGetValue(seat.SeatId, out var current))
            {
                current.Price = seat.Price;
                continue;
            }

            db.EventSeats.Add(new EventSeat
            {
                EventId = message.EventId,
                SeatId = seat.SeatId,
                Section = seat.Section,
                Row = seat.Row,
                Number = seat.Number,
                Price = seat.Price,
            });
        }
    }
}

/// <summary>Saga step: payment captured. Confirm the booking, or compensate with a refund if it was already given up.</summary>
internal sealed class PaymentSucceededHandler(
    BookingDbContext db,
    IOutbox outbox,
    SeatLockService locks,
    BookingMetrics metrics,
    TimeProvider time) : IMessageHandler<PaymentSucceeded>
{
    public async Task HandleAsync(PaymentSucceeded message, MessageContext context, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings.Include(b => b.Seats).SingleOrDefaultAsync(b => b.Id == message.BookingId, cancellationToken)
            ?? throw new InvalidOperationException($"Payment for unknown booking {message.BookingId}.");

        switch (booking.ApplyPaymentSucceeded(message.PaymentId, time.GetUtcNow()))
        {
            case PaymentApplication.Confirmed:
                outbox.Enqueue(BookingEvents.Confirmed(booking), booking.Id.ToString());
                metrics.Transition(BookingStatus.Confirmed);
                // The seats are now sold in PostgreSQL; the temporary Redis locks are no longer needed.
                context.OnCommitted(() => locks.ReleaseAsync(booking.EventId, booking.SeatIds, booking.Id));
                break;

            case PaymentApplication.RefundRequired:
                outbox.Enqueue(new RefundPayment(booking.Id, message.PaymentId, booking.StatusReason!), booking.Id.ToString());
                metrics.Transition(BookingStatus.RefundPending);
                break;

            case PaymentApplication.AlreadyApplied:
                break;
        }
    }
}

/// <summary>Saga step: payment declined. Compensate the hold by releasing the seats.</summary>
internal sealed class PaymentFailedHandler(
    BookingDbContext db,
    IOutbox outbox,
    SeatLockService locks,
    BookingMetrics metrics,
    TimeProvider time) : IMessageHandler<PaymentFailed>
{
    public async Task HandleAsync(PaymentFailed message, MessageContext context, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings.Include(b => b.Seats).SingleOrDefaultAsync(b => b.Id == message.BookingId, cancellationToken)
            ?? throw new InvalidOperationException($"Payment failure for unknown booking {message.BookingId}.");

        if (booking.ApplyPaymentFailed($"Payment failed: {message.Reason}", time.GetUtcNow()))
        {
            outbox.Enqueue(BookingEvents.Cancelled(booking), booking.Id.ToString());
            metrics.Transition(BookingStatus.PaymentFailed);
            context.OnCommitted(() => locks.ReleaseAsync(booking.EventId, booking.SeatIds, booking.Id));
        }
    }
}

/// <summary>Saga step: the compensating refund went through.</summary>
internal sealed class PaymentRefundedHandler(
    BookingDbContext db,
    IOutbox outbox,
    BookingMetrics metrics,
    TimeProvider time) : IMessageHandler<PaymentRefunded>
{
    public async Task HandleAsync(PaymentRefunded message, MessageContext context, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings.Include(b => b.Seats).SingleOrDefaultAsync(b => b.Id == message.BookingId, cancellationToken)
            ?? throw new InvalidOperationException($"Refund for unknown booking {message.BookingId}.");

        if (booking.ApplyRefunded(time.GetUtcNow()))
        {
            outbox.Enqueue(BookingEvents.Refunded(booking), booking.Id.ToString());
            metrics.Transition(BookingStatus.Refunded);
        }
    }
}
