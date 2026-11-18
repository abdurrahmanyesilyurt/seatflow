using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeatFlow.Bookings.Data;
using SeatFlow.Bookings.Domain;
using SeatFlow.Bookings.Seats;
using SeatFlow.Messaging.Outbox;

namespace SeatFlow.Bookings.Workers;

/// <summary>
/// Saga timeout. Expires holds that were never checked out and payments that never answered, releases their seats
/// and publishes <c>booking.expired</c>. Each booking is saved on its own with optimistic concurrency, so it is safe
/// to run on every replica and it never overrides a payment reply that landed a moment earlier.
/// </summary>
internal sealed partial class HoldExpiryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<BookingOptions> options,
    TimeProvider time,
    ILogger<HoldExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ExpiryScanInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var expired = await ExpireDueBookingsAsync(stoppingToken);
                if (expired > 0)
                {
                    LogExpired(expired);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogScanFailed(ex);
            }
        }
    }

    internal async Task<int> ExpireDueBookingsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var locks = scope.ServiceProvider.GetRequiredService<SeatLockService>();
        var metrics = scope.ServiceProvider.GetRequiredService<BookingMetrics>();

        var now = time.GetUtcNow();
        var due = await db.Bookings
            .Where(b => (b.Status == BookingStatus.Held && b.HoldExpiresAt <= now)
                     || (b.Status == BookingStatus.PaymentPending && b.PaymentDeadline <= now))
            .OrderBy(b => b.HoldExpiresAt)
            .Select(b => b.Id)
            .Take(100)
            .ToListAsync(cancellationToken);

        // A span only when there is work, so the five-second scan does not flood the tracing backend.
        using var activity = due.Count > 0 ? BookingMetrics.ActivitySource.StartActivity("expire due bookings") : null;
        activity?.SetTag("seatflow.bookings.due", due.Count);

        var expired = 0;
        foreach (var id in due)
        {
            db.ChangeTracker.Clear();
            var booking = await db.Bookings.Include(b => b.Seats).SingleAsync(b => b.Id == id, cancellationToken);
            if (!booking.Expire(time.GetUtcNow()))
            {
                continue;
            }

            outbox.Enqueue(BookingEvents.Expired(booking), booking.Id.ToString());
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                continue; // a payment reply or the customer got there first
            }

            expired++;
            metrics.Transition(BookingStatus.Expired);
            await locks.ReleaseAsync(booking.EventId, booking.SeatIds, booking.Id);
        }

        return expired;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} booking(s)")]
    private partial void LogExpired(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Expiry scan failed")]
    private partial void LogScanFailed(Exception exception);
}
