namespace SeatFlow.Bookings.Domain;

public enum BookingStatus
{
    Held,
    PaymentPending,
    Confirmed,
    PaymentFailed,
    Expired,
    Cancelled,
    RefundPending,
    Refunded,
}

public enum PaymentApplication
{
    Confirmed,
    AlreadyApplied,
    RefundRequired,
}

public sealed class BookingStateException(string message) : Exception(message);

/// <summary>
/// The booking aggregate is also the saga state. Every transition is a method here, guarded by the current status,
/// and persisted with optimistic concurrency (PostgreSQL <c>xmin</c>), so concurrent actors (the customer, the expiry
/// worker, payment replies) cannot both win.
/// <code>
/// Held ──checkout──▶ PaymentPending ──payment.succeeded──▶ Confirmed
///  │                    │  └─payment.failed──▶ PaymentFailed (seats released)
///  │                    └─deadline passed──▶ Expired ──late payment.succeeded──▶ RefundPending ──▶ Refunded
///  ├─ttl passed──▶ Expired
///  └─customer──▶ Cancelled
/// </code>
/// </summary>
public sealed class Booking
{
    private readonly List<BookingSeat> _seats = [];

    private Booking()
    {
    }

    public Guid Id { get; private set; }

    public string UserId { get; private set; } = "";

    public string UserEmail { get; private set; } = "";

    public Guid EventId { get; private set; }

    public string EventTitle { get; private set; } = "";

    public DateTimeOffset EventStartsAt { get; private set; }

    public BookingStatus Status { get; private set; }

    public decimal TotalAmount { get; private set; }

    public string Currency { get; private set; } = "";

    /// <summary>Hash of the original request, to reject an idempotency key reused with a different payload.</summary>
    public string RequestHash { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset HoldExpiresAt { get; private set; }

    public DateTimeOffset? PaymentDeadline { get; private set; }

    public Guid? PaymentId { get; private set; }

    public string? StatusReason { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Mapped to PostgreSQL's <c>xmin</c> system column: the optimistic-concurrency token.</summary>
    public uint Version { get; private set; }

    public IReadOnlyList<BookingSeat> Seats => _seats;

    public IReadOnlyList<string> SeatIds => _seats.Select(s => s.SeatId).ToList();

    public static Booking Hold(
        Guid id,
        string userId,
        string userEmail,
        EventInfo evt,
        IReadOnlyList<EventSeat> seats,
        string requestHash,
        DateTimeOffset now,
        TimeSpan holdTtl)
    {
        if (seats.Count == 0)
        {
            throw new BookingStateException("A booking needs at least one seat.");
        }

        var booking = new Booking
        {
            Id = id,
            UserId = userId,
            UserEmail = userEmail,
            EventId = evt.Id,
            EventTitle = evt.Title,
            EventStartsAt = evt.StartsAt,
            Status = BookingStatus.Held,
            Currency = evt.Currency,
            TotalAmount = seats.Sum(s => s.Price),
            RequestHash = requestHash,
            CreatedAt = now,
            UpdatedAt = now,
            HoldExpiresAt = now + holdTtl,
        };
        booking._seats.AddRange(seats.Select(s => new BookingSeat(id, evt.Id, s.SeatId, s.Price)));
        return booking;
    }

    public bool IsExpiredAt(DateTimeOffset now) =>
        (Status == BookingStatus.Held && now >= HoldExpiresAt) ||
        (Status == BookingStatus.PaymentPending && now >= PaymentDeadline);

    public void StartPayment(DateTimeOffset now, TimeSpan paymentTimeout)
    {
        if (Status != BookingStatus.Held)
        {
            throw new BookingStateException($"Cannot check out a booking that is {Status}.");
        }

        if (now >= HoldExpiresAt)
        {
            throw new BookingStateException("The hold has expired.");
        }

        Status = BookingStatus.PaymentPending;
        PaymentDeadline = now + paymentTimeout;
        UpdatedAt = now;
    }

    public PaymentApplication ApplyPaymentSucceeded(Guid paymentId, DateTimeOffset now)
    {
        switch (Status)
        {
            case BookingStatus.PaymentPending:
                Status = BookingStatus.Confirmed;
                PaymentId = paymentId;
                StatusReason = null;
                UpdatedAt = now;
                return PaymentApplication.Confirmed;

            case BookingStatus.Confirmed:
            case BookingStatus.RefundPending:
            case BookingStatus.Refunded:
                return PaymentApplication.AlreadyApplied;

            default:
                // The money arrived after the booking was given up (expired, failed or cancelled) and the seats may
                // already belong to someone else. Compensate: refund instead of resurrecting the booking.
                StatusReason = $"Payment completed after the booking was {Status}; refunding.";
                Status = BookingStatus.RefundPending;
                PaymentId = paymentId;
                UpdatedAt = now;
                return PaymentApplication.RefundRequired;
        }
    }

    public bool ApplyPaymentFailed(string reason, DateTimeOffset now)
    {
        if (Status != BookingStatus.PaymentPending)
        {
            return false;
        }

        Status = BookingStatus.PaymentFailed;
        StatusReason = reason;
        ReleaseSeats(now);
        return true;
    }

    public bool ApplyRefunded(DateTimeOffset now)
    {
        if (Status != BookingStatus.RefundPending)
        {
            return false;
        }

        Status = BookingStatus.Refunded;
        UpdatedAt = now;
        return true;
    }

    public bool Expire(DateTimeOffset now)
    {
        if (!IsExpiredAt(now))
        {
            return false;
        }

        StatusReason = Status == BookingStatus.Held ? "Hold expired before checkout." : "Payment did not complete in time.";
        Status = BookingStatus.Expired;
        ReleaseSeats(now);
        return true;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status != BookingStatus.Held)
        {
            throw new BookingStateException($"Only a held booking can be cancelled; this one is {Status}.");
        }

        Status = BookingStatus.Cancelled;
        StatusReason = "Cancelled by the customer.";
        ReleaseSeats(now);
    }

    private void ReleaseSeats(DateTimeOffset now)
    {
        foreach (var seat in _seats)
        {
            seat.Release();
        }

        UpdatedAt = now;
    }
}

/// <summary>
/// One row per seat in a booking. A partial unique index on (event_id, seat_id) WHERE is_active makes a double sale
/// impossible at the database level, whatever happens to the Redis locks in front of it.
/// </summary>
public sealed class BookingSeat
{
    private BookingSeat()
    {
    }

    internal BookingSeat(Guid bookingId, Guid eventId, string seatId, decimal price)
    {
        BookingId = bookingId;
        EventId = eventId;
        SeatId = seatId;
        Price = price;
        IsActive = true;
    }

    public Guid BookingId { get; private set; }

    public Guid EventId { get; private set; }

    public string SeatId { get; private set; } = "";

    public decimal Price { get; private set; }

    public bool IsActive { get; private set; }

    internal void Release() => IsActive = false;
}
