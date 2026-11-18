using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeatFlow.Bookings.Data;
using SeatFlow.Bookings.Domain;
using SeatFlow.Bookings.Seats;
using SeatFlow.Messaging.Outbox;
using SeatFlow.ServiceDefaults;

namespace SeatFlow.Bookings.Features;

/// <summary>
/// POST /api/bookings/{id}/checkout: starts the payment saga. The status change and the <c>payment.process</c>
/// command commit in one transaction (outbox), so there is no window where the booking says "paying" but no command
/// was sent, or a command was sent for a booking that rolled back. Repeating the call is harmless.
/// </summary>
internal sealed partial class CheckoutHandler(
    BookingDbContext db,
    SeatLockService locks,
    IOutbox outbox,
    BookingMetrics metrics,
    IOptions<BookingOptions> options,
    TimeProvider time,
    ILogger<CheckoutHandler> logger)
{
    public async Task<IResult> HandleAsync(Guid id, CheckoutRequest request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PaymentToken) || request.PaymentToken.Length > 64)
        {
            return BookingProblems.Validation("paymentToken", "A payment token (max 64 characters) is required.");
        }

        var userId = user.GetUserId();
        var booking = await db.Bookings.Include(b => b.Seats).SingleOrDefaultAsync(b => b.Id == id && b.UserId == userId, cancellationToken);
        if (booking is null)
        {
            return TypedResults.NotFound();
        }

        switch (booking.Status)
        {
            case BookingStatus.PaymentPending:
                return TypedResults.Accepted($"/api/bookings/{id}", BookingResponse.From(booking));
            case BookingStatus.Confirmed:
                return TypedResults.Ok(BookingResponse.From(booking));
            case not BookingStatus.Held:
                return BookingProblems.InvalidState($"This booking is {booking.Status} and can no longer be paid.");
        }

        var now = time.GetUtcNow();
        if (booking.Expire(now))
        {
            outbox.Enqueue(BookingEvents.Expired(booking), booking.Id.ToString());
            await db.SaveChangesAsync(cancellationToken);
            metrics.Transition(BookingStatus.Expired);
            await locks.ReleaseAsync(booking.EventId, booking.SeatIds, booking.Id);
            return BookingProblems.InvalidState("The hold expired before checkout. Please pick your seats again.");
        }

        var paymentTimeout = options.Value.PaymentTimeout;
        booking.StartPayment(now, paymentTimeout);
        outbox.Enqueue(BookingEvents.ProcessPayment(booking, request.PaymentToken), booking.Id.ToString());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return BookingProblems.InvalidState("The booking changed while checking out. Fetch it and try again.");
        }

        // Keep the Redis locks alive for the payment window (same owner, so this just refreshes the TTL).
        var refreshed = await locks.TryAcquireAsync(booking.EventId, booking.SeatIds, booking.Id, paymentTimeout + TimeSpan.FromSeconds(30));
        if (refreshed.Outcome == SeatLockOutcome.Conflict)
        {
            LogLockLost(booking.Id); // the database still owns the seats; this only affects the fast path
        }

        metrics.Transition(BookingStatus.PaymentPending);
        return TypedResults.Accepted($"/api/bookings/{id}", BookingResponse.From(booking));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis lock for booking {BookingId} was taken over; relying on the database constraint")]
    private partial void LogLockLost(Guid bookingId);
}
