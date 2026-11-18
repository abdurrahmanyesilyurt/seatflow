using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using SeatFlow.Bookings.Data;
using SeatFlow.Bookings.Domain;
using SeatFlow.Bookings.Seats;
using SeatFlow.Messaging.Outbox;
using SeatFlow.ServiceDefaults;

namespace SeatFlow.Bookings.Features;

/// <summary>
/// POST /api/bookings: hold seats. The race for a seat is decided in three layers:
/// <list type="number">
/// <item>PostgreSQL read: seats already sold or held (and not expired) are rejected without touching Redis.</item>
/// <item>Redis Lua script: atomic all-or-nothing lock across all requested seats, with a TTL, for the contention window.</item>
/// <item>PostgreSQL partial unique index: the insert itself cannot create a second active booking for a seat.</item>
/// </list>
/// Idempotency: the booking id is derived from (user, Idempotency-Key), so a retry replays the same booking.
/// </summary>
internal sealed partial class HoldSeatsHandler(
    BookingDbContext db,
    SeatLockService locks,
    IOutbox outbox,
    BookingMetrics metrics,
    IOptions<BookingOptions> options,
    TimeProvider time,
    ILogger<HoldSeatsHandler> logger)
{
    public async Task<IResult> HandleAsync(
        HoldSeatsRequest request,
        string? idempotencyKey,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length is < 8 or > 100)
        {
            return BookingProblems.Validation("Idempotency-Key", "Header is required (8-100 characters).");
        }

        var seatIds = (request.SeatIds ?? []).Select(s => s.Trim()).ToList();
        if (seatIds.Count == 0 || seatIds.Count > options.Value.MaxSeatsPerBooking)
        {
            return BookingProblems.Validation("seatIds", $"Pick between 1 and {options.Value.MaxSeatsPerBooking} seats.");
        }

        if (seatIds.Distinct(StringComparer.Ordinal).Count() != seatIds.Count)
        {
            return BookingProblems.Validation("seatIds", "Seats must not repeat.");
        }

        var userId = user.GetUserId();
        var bookingId = BookingIdentity.FromIdempotencyKey(userId, idempotencyKey);
        var requestHash = BookingIdentity.RequestHash(request.EventId, seatIds);

        // Replay: this exact request was already accepted.
        var existing = await db.Bookings.AsNoTracking().Include(b => b.Seats).SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, requestHash);
        }

        var evt = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == request.EventId, cancellationToken);
        if (evt is null)
        {
            return TypedResults.Problem(title: "Event not found", statusCode: StatusCodes.Status404NotFound);
        }

        var seats = await db.EventSeats.AsNoTracking()
            .Where(s => s.EventId == request.EventId && seatIds.Contains(s.SeatId))
            .ToListAsync(cancellationToken);
        if (seats.Count != seatIds.Count)
        {
            var unknown = seatIds.Except(seats.Select(s => s.SeatId));
            return BookingProblems.Validation("seatIds", $"Unknown seat(s): {string.Join(", ", unknown)}.");
        }

        // Layer 1: the database view. Expired holds found on the way are expired right here (lazy expiry),
        // so a seat frees up the moment its hold lapses, not on the worker's next tick.
        var (blocked, self) = await ExpireStaleBlockersAsync(request.EventId, seatIds, bookingId, cancellationToken);
        if (self is not null)
        {
            // A concurrent retry of this very request committed between our replay check and now.
            return Replay(self, requestHash);
        }

        if (blocked.Count > 0)
        {
            metrics.HoldAttempt("conflict");
            return BookingProblems.SeatsUnavailable(blocked);
        }

        // Layer 2: Redis, atomic across all seats.
        var holdTtl = options.Value.HoldTtl;
        var lockResult = await locks.TryAcquireAsync(request.EventId, seatIds, bookingId, holdTtl);
        if (lockResult.Outcome == SeatLockOutcome.Conflict)
        {
            metrics.HoldAttempt("conflict");
            return BookingProblems.SeatsUnavailable(lockResult.ConflictingSeats);
        }

        // Layer 3: the insert, guarded by the partial unique index.
        var booking = Booking.Hold(bookingId, userId, user.GetEmail(), evt, seats, requestHash, time.GetUtcNow(), holdTtl);
        db.Bookings.Add(booking);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, BookingDbContext.PrimaryKeyName))
        {
            // A concurrent retry with the same Idempotency-Key won the insert; replay its result.
            db.ChangeTracker.Clear();
            var winner = await db.Bookings.AsNoTracking().Include(b => b.Seats).SingleAsync(b => b.Id == bookingId, cancellationToken);
            return Replay(winner, requestHash);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, BookingDbContext.ActiveSeatIndexName))
        {
            // Redis let us through (lock lost, or Redis down) but the database says the seat is taken.
            LogDatabaseRejectedHold(bookingId);
            await locks.ReleaseAsync(request.EventId, seatIds, bookingId);
            metrics.HoldAttempt("conflict");
            return BookingProblems.SeatsUnavailable(seatIds);
        }

        metrics.HoldAttempt(lockResult.Outcome == SeatLockOutcome.RedisUnavailable ? "created_without_redis" : "created");
        metrics.Transition(BookingStatus.Held);
        return TypedResults.Created($"/api/bookings/{booking.Id}", BookingResponse.From(booking));
    }

    private async Task<(List<string> Blocked, Booking? Self)> ExpireStaleBlockersAsync(
        Guid eventId, List<string> seatIds, Guid bookingId, CancellationToken cancellationToken)
    {
        var blockers = await db.Bookings.Include(b => b.Seats)
            .Where(b => b.EventId == eventId && b.Seats.Any(s => s.IsActive && seatIds.Contains(s.SeatId)))
            .ToListAsync(cancellationToken);

        if (blockers.Find(b => b.Id == bookingId) is { } self)
        {
            return ([], self);
        }

        var now = time.GetUtcNow();
        var blocked = new List<string>();
        var expired = new List<Booking>();

        foreach (var blocker in blockers)
        {
            if (blocker.Expire(now))
            {
                outbox.Enqueue(BookingEvents.Expired(blocker), blocker.Id.ToString());
                expired.Add(blocker);
            }
            else
            {
                blocked.AddRange(blocker.SeatIds.Intersect(seatIds));
            }
        }

        if (expired.Count == 0)
        {
            db.ChangeTracker.Clear();
            return (blocked, null);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The worker or a payment reply changed one of them concurrently; report the seats as taken.
            db.ChangeTracker.Clear();
            return ([.. blocked, .. expired.SelectMany(b => b.SeatIds).Intersect(seatIds)], null);
        }

        foreach (var booking in expired)
        {
            metrics.Transition(BookingStatus.Expired);
            await locks.ReleaseAsync(booking.EventId, booking.SeatIds, booking.Id);
        }

        db.ChangeTracker.Clear();
        return (blocked, null);
    }

    private static IResult Replay(Booking existing, string requestHash)
    {
        if (existing.RequestHash != requestHash)
        {
            return TypedResults.Problem(
                title: "Idempotency key reused",
                detail: "This Idempotency-Key was already used with a different request body.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return new ReplayedResult(BookingResponse.From(existing));
    }

    private static bool IsUniqueViolation(DbUpdateException ex, string constraint) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg && pg.ConstraintName == constraint;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database constraint rejected hold {BookingId} after the Redis lock was granted")]
    private partial void LogDatabaseRejectedHold(Guid bookingId);

    /// <summary>200 OK with the original body plus <c>Idempotent-Replayed: true</c>.</summary>
    private sealed class ReplayedResult(BookingResponse body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers["Idempotent-Replayed"] = "true";
            return TypedResults.Ok(body).ExecuteAsync(httpContext);
        }
    }
}
