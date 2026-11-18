using SeatFlow.Bookings.Domain;

namespace SeatFlow.Bookings.UnitTests;

/// <summary>The saga's state machine, without any infrastructure.</summary>
public sealed class BookingSagaTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HoldTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PaymentTimeout = TimeSpan.FromMinutes(2);

    private static Booking NewHold(params string[] seats)
    {
        var evt = new EventInfo { Id = Guid.NewGuid(), Title = "Jazz", Currency = "TRY", StartsAt = Now.AddDays(30) };
        var eventSeats = seats.Select(s => new EventSeat { EventId = evt.Id, SeatId = s, Price = 100m }).ToList();
        return Booking.Hold(Guid.NewGuid(), "usr_1", "a@example.com", evt, eventSeats, "hash", Now, HoldTtl);
    }

    [Fact]
    public void Hold_reserves_every_seat_and_sums_the_price()
    {
        var booking = NewHold("A-1-1", "A-1-2");

        Assert.Equal(BookingStatus.Held, booking.Status);
        Assert.Equal(200m, booking.TotalAmount);
        Assert.Equal(Now + HoldTtl, booking.HoldExpiresAt);
        Assert.All(booking.Seats, s => Assert.True(s.IsActive));
    }

    [Fact]
    public void Successful_payment_confirms_and_keeps_the_seats()
    {
        var booking = NewHold("A-1-1");
        booking.StartPayment(Now.AddMinutes(1), PaymentTimeout);

        var result = booking.ApplyPaymentSucceeded(Guid.NewGuid(), Now.AddMinutes(2));

        Assert.Equal(PaymentApplication.Confirmed, result);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.All(booking.Seats, s => Assert.True(s.IsActive));
    }

    [Fact]
    public void Duplicate_payment_reply_is_a_no_op()
    {
        var booking = NewHold("A-1-1");
        var paymentId = Guid.NewGuid();
        booking.StartPayment(Now, PaymentTimeout);
        booking.ApplyPaymentSucceeded(paymentId, Now);

        Assert.Equal(PaymentApplication.AlreadyApplied, booking.ApplyPaymentSucceeded(paymentId, Now));
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
    }

    [Fact]
    public void Failed_payment_releases_the_seats()
    {
        var booking = NewHold("A-1-1", "A-1-2");
        booking.StartPayment(Now, PaymentTimeout);

        Assert.True(booking.ApplyPaymentFailed("card_declined", Now));
        Assert.Equal(BookingStatus.PaymentFailed, booking.Status);
        Assert.All(booking.Seats, s => Assert.False(s.IsActive));
    }

    [Fact]
    public void Payment_after_the_deadline_requires_a_refund_instead_of_confirming()
    {
        var booking = NewHold("A-1-1");
        booking.StartPayment(Now, PaymentTimeout);
        Assert.True(booking.Expire(Now + PaymentTimeout));

        var result = booking.ApplyPaymentSucceeded(Guid.NewGuid(), Now + PaymentTimeout + TimeSpan.FromSeconds(5));

        Assert.Equal(PaymentApplication.RefundRequired, result);
        Assert.Equal(BookingStatus.RefundPending, booking.Status);
        Assert.All(booking.Seats, s => Assert.False(s.IsActive)); // the seats stay released

        Assert.True(booking.ApplyRefunded(Now.AddMinutes(5)));
        Assert.Equal(BookingStatus.Refunded, booking.Status);
    }

    [Fact]
    public void A_hold_cannot_expire_early_or_be_paid_after_expiry()
    {
        var booking = NewHold("A-1-1");

        Assert.False(booking.Expire(Now + HoldTtl - TimeSpan.FromSeconds(1)));
        Assert.Throws<BookingStateException>(() => booking.StartPayment(Now + HoldTtl, PaymentTimeout));
        Assert.True(booking.Expire(Now + HoldTtl));
        Assert.Equal(BookingStatus.Expired, booking.Status);
    }

    [Fact]
    public void Only_a_held_booking_can_be_cancelled()
    {
        var booking = NewHold("A-1-1");
        booking.StartPayment(Now, PaymentTimeout);

        Assert.Throws<BookingStateException>(() => booking.Cancel(Now));
    }
}

public sealed class BookingIdentityTests
{
    [Fact]
    public void Same_user_and_key_always_give_the_same_booking_id()
    {
        var first = BookingIdentity.FromIdempotencyKey("usr_1", "key-123456");
        var second = BookingIdentity.FromIdempotencyKey("usr_1", "key-123456");

        Assert.Equal(first, second);
        Assert.Equal(5, first.Version);
    }

    [Fact]
    public void Different_users_with_the_same_key_get_different_ids()
    {
        Assert.NotEqual(
            BookingIdentity.FromIdempotencyKey("usr_1", "key-123456"),
            BookingIdentity.FromIdempotencyKey("usr_2", "key-123456"));
    }

    [Fact]
    public void Matches_the_RFC_4122_reference_vector()
    {
        // uuid5(NAMESPACE_DNS, "python.org") == 886313e1-3b8a-5372-9b90-0c9aee199e5d
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        Assert.Equal(new Guid("886313e1-3b8a-5372-9b90-0c9aee199e5d"), BookingIdentity.CreateV5(dns, "python.org"));
    }

    [Fact]
    public void Request_hash_ignores_seat_order()
    {
        var eventId = Guid.NewGuid();
        Assert.Equal(
            BookingIdentity.RequestHash(eventId, ["A-1-2", "A-1-1"]),
            BookingIdentity.RequestHash(eventId, ["A-1-1", "A-1-2"]));
    }
}
