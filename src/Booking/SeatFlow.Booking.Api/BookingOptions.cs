using System.Diagnostics;
using System.Diagnostics.Metrics;
using SeatFlow.Bookings.Domain;
using SeatFlow.Contracts;

namespace SeatFlow.Bookings;

public sealed class BookingOptions
{
    public const string SectionName = "Booking";

    /// <summary>How long seats stay reserved before checkout. Also the Redis lock TTL.</summary>
    public TimeSpan HoldTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long the saga waits for a payment reply before giving the seats back.</summary>
    public TimeSpan PaymentTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan ExpiryScanInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxSeatsPerBooking { get; set; } = 8;
}

public sealed class BookingMetrics
{
    public const string MeterName = "SeatFlow.Booking";

    /// <summary>Spans for Booking's own background work (picked up by the "SeatFlow.*" source filter).</summary>
    public static readonly ActivitySource ActivitySource = new(MeterName);

    private readonly Counter<long> _holds;
    private readonly Counter<long> _transitions;

    public BookingMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _holds = meter.CreateCounter<long>("seatflow.booking.holds", description: "Seat hold attempts by outcome.");
        _transitions = meter.CreateCounter<long>("seatflow.booking.transitions", description: "Booking saga transitions by target status.");
    }

    public void HoldAttempt(string outcome) => _holds.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void Transition(BookingStatus status) => _transitions.Add(1, new KeyValuePair<string, object?>("status", status.ToString()));
}

/// <summary>Builds the integration events the booking saga publishes.</summary>
internal static class BookingEvents
{
    public static BookingConfirmed Confirmed(Booking b) =>
        new(b.Id, b.UserId, b.UserEmail, b.EventId, b.EventTitle, b.EventStartsAt, b.SeatIds, b.TotalAmount, b.Currency);

    public static BookingCancelled Cancelled(Booking b) =>
        new(b.Id, b.UserId, b.UserEmail, b.EventTitle, b.SeatIds, b.StatusReason ?? "Cancelled");

    public static BookingExpired Expired(Booking b) =>
        new(b.Id, b.UserId, b.UserEmail, b.EventTitle, b.SeatIds);

    public static BookingRefunded Refunded(Booking b) =>
        new(b.Id, b.UserId, b.UserEmail, b.EventTitle, b.TotalAmount, b.Currency, b.StatusReason ?? "Refunded");

    public static ProcessPayment ProcessPayment(Booking b, string paymentToken) =>
        new(b.Id, b.UserId, b.TotalAmount, b.Currency, paymentToken);
}
