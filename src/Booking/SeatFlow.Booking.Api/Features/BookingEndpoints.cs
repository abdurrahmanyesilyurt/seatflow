using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SeatFlow.Bookings.Data;
using SeatFlow.Bookings.Domain;
using SeatFlow.Bookings.Seats;
using SeatFlow.Messaging.Outbox;
using SeatFlow.ServiceDefaults;

namespace SeatFlow.Bookings.Features;

public static class BookingEndpoints
{
    public static IEndpointRouteBuilder MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/bookings").WithTags("Bookings");

        group.MapGet("/events/{eventId:guid}/seats", GetAvailabilityAsync).AllowAnonymous();

        var mine = group.MapGroup("").RequireAuthorization();
        mine.MapPost("", (HoldSeatsRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                ClaimsPrincipal user, HoldSeatsHandler handler, CancellationToken ct) =>
            handler.HandleAsync(request, idempotencyKey, user, ct));
        mine.MapPost("/{id:guid}/checkout", (Guid id, CheckoutRequest request, ClaimsPrincipal user, CheckoutHandler handler,
                CancellationToken ct) =>
            handler.HandleAsync(id, request, user, ct));
        mine.MapGet("", ListMineAsync);
        mine.MapGet("/{id:guid}", GetAsync);
        mine.MapDelete("/{id:guid}", CancelAsync);

        return app;
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, BookingDbContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var booking = await db.Bookings.AsNoTracking().Include(b => b.Seats)
            .SingleOrDefaultAsync(b => b.Id == id && b.UserId == userId, ct);
        return booking is null ? TypedResults.NotFound() : TypedResults.Ok(BookingResponse.From(booking));
    }

    private static async Task<IReadOnlyList<BookingResponse>> ListMineAsync(ClaimsPrincipal user, BookingDbContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var bookings = await db.Bookings.AsNoTracking().Include(b => b.Seats)
            .Where(b => b.UserId == userId)
            .OrderByDescending(b => b.CreatedAt)
            .Take(20)
            .ToListAsync(ct);
        return bookings.Select(BookingResponse.From).ToList();
    }

    private static async Task<IResult> CancelAsync(
        Guid id, ClaimsPrincipal user, BookingDbContext db, IOutbox outbox, SeatLockService locks, BookingMetrics metrics,
        TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var booking = await db.Bookings.Include(b => b.Seats).SingleOrDefaultAsync(b => b.Id == id && b.UserId == userId, ct);
        if (booking is null)
        {
            return TypedResults.NotFound();
        }

        if (booking.Status is BookingStatus.Cancelled or BookingStatus.Expired)
        {
            return TypedResults.Ok(BookingResponse.From(booking)); // already released: cancelling again is a no-op
        }

        try
        {
            booking.Cancel(time.GetUtcNow());
        }
        catch (BookingStateException ex)
        {
            return BookingProblems.InvalidState(ex.Message);
        }

        outbox.Enqueue(BookingEvents.Cancelled(booking), booking.Id.ToString());
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return BookingProblems.InvalidState("The booking changed while cancelling. Fetch it and try again.");
        }

        metrics.Transition(BookingStatus.Cancelled);
        await locks.ReleaseAsync(booking.EventId, booking.SeatIds, booking.Id);
        return TypedResults.Ok(BookingResponse.From(booking));
    }

    private static async Task<IResult> GetAvailabilityAsync(Guid eventId, BookingDbContext db, TimeProvider time, CancellationToken ct)
    {
        var evt = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, ct);
        if (evt is null)
        {
            return TypedResults.NotFound();
        }

        var seats = await db.EventSeats.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .OrderBy(s => s.Section).ThenBy(s => s.Row).ThenBy(s => s.Number)
            .ToListAsync(ct);

        var taken = await db.BookingSeats.AsNoTracking()
            .Where(s => s.EventId == eventId && s.IsActive)
            .Join(db.Bookings, s => s.BookingId, b => b.Id, (s, b) => new { s.SeatId, b.Status, b.HoldExpiresAt, b.PaymentDeadline })
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        var statusBySeat = new Dictionary<string, SeatStatus>(StringComparer.Ordinal);
        foreach (var t in taken)
        {
            var status = t.Status switch
            {
                BookingStatus.Confirmed => SeatStatus.Sold,
                BookingStatus.Held when t.HoldExpiresAt > now => SeatStatus.Held,
                BookingStatus.PaymentPending when t.PaymentDeadline > now => SeatStatus.Held,
                _ => SeatStatus.Available, // lapsed, waiting for the expiry worker
            };
            if (status != SeatStatus.Available)
            {
                statusBySeat[t.SeatId] = status;
            }
        }

        var result = seats
            .Select(s => new SeatAvailability(s.SeatId, s.Section, s.Row, s.Number, s.Price,
                statusBySeat.GetValueOrDefault(s.SeatId, SeatStatus.Available)))
            .ToList();

        return TypedResults.Ok(new EventAvailability(
            evt.Id, evt.Title, evt.StartsAt, evt.Currency,
            result.Count(s => s.Status == SeatStatus.Available),
            result.Count(s => s.Status == SeatStatus.Held),
            result.Count(s => s.Status == SeatStatus.Sold),
            result));
    }
}
