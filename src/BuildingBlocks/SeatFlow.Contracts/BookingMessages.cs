namespace SeatFlow.Contracts;

// Booking lifecycle events. They are notifications (not Required): if nobody listens yet, dropping them is fine.

[Message("booking.confirmed")]
public sealed record BookingConfirmed(
    Guid BookingId,
    string UserId,
    string UserEmail,
    Guid EventId,
    string EventTitle,
    DateTimeOffset StartsAt,
    IReadOnlyList<string> SeatIds,
    decimal Total,
    string Currency);

[Message("booking.cancelled")]
public sealed record BookingCancelled(
    Guid BookingId,
    string UserId,
    string UserEmail,
    string EventTitle,
    IReadOnlyList<string> SeatIds,
    string Reason);

[Message("booking.expired")]
public sealed record BookingExpired(
    Guid BookingId,
    string UserId,
    string UserEmail,
    string EventTitle,
    IReadOnlyList<string> SeatIds);

[Message("booking.refunded")]
public sealed record BookingRefunded(
    Guid BookingId,
    string UserId,
    string UserEmail,
    string EventTitle,
    decimal Amount,
    string Currency,
    string Reason);
