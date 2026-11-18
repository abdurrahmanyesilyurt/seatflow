using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeatFlow.Bookings.Data;
using SeatFlow.Bookings.Domain;
using SeatFlow.Bookings.Features;
using SeatFlow.Contracts;
using SeatFlow.Messaging.Outbox;
using static SeatFlow.Bookings.IntegrationTests.BookingHarness;

namespace SeatFlow.Bookings.IntegrationTests;

/// <summary>Booking as the saga orchestrator; the test bus plays the Payment service.</summary>
public sealed class PaymentSagaTests(DefaultBookingFixture fixture) : IClassFixture<DefaultBookingFixture>
{
    private BookingHarness H => fixture.Harness;

    [Fact]
    public async Task Checkout_commands_a_payment_and_success_confirms_the_booking()
    {
        var (eventId, seats) = await H.SeedEventAsync(price: 250m);
        var alice = H.ClientFor("alice");
        var booking = await ReadAsync(await HoldAsync(alice, eventId, seats[..2]));

        var checkout = await CheckoutAsync(alice, booking.Id);
        Assert.Equal(HttpStatusCode.Accepted, checkout.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await CheckoutAsync(alice, booking.Id)).StatusCode); // repeat is harmless

        // The command left through the outbox exactly once, with the server-side total.
        var command = await H.Bus.WaitForAsync<ProcessPayment>(c => c.BookingId == booking.Id);
        Assert.Equal(500m, command.Amount);
        Assert.Equal("tok_visa", command.PaymentToken);

        await H.Bus.PublishAsync(new PaymentSucceeded(booking.Id, Guid.NewGuid(), 500m, "TRY"));

        var confirmed = await WaitForStatusAsync(alice, booking.Id, BookingStatus.Confirmed);
        Assert.NotNull(confirmed);
        var notice = await H.Bus.WaitForAsync<BookingConfirmed>(e => e.BookingId == booking.Id);
        Assert.Equal("alice@example.com", notice.UserEmail);
        Assert.Equal(2, (await H.SeatMapAsync(eventId)).Sold);
        Assert.Single(H.Bus.Received<ProcessPayment>(c => c.BookingId == booking.Id));
    }

    [Fact]
    public async Task A_declined_payment_releases_the_seats()
    {
        var (eventId, seats) = await H.SeedEventAsync();
        var alice = H.ClientFor("alice");
        var booking = await ReadAsync(await HoldAsync(alice, eventId, [seats[0]]));
        await CheckoutAsync(alice, booking.Id);
        await H.Bus.WaitForAsync<ProcessPayment>(c => c.BookingId == booking.Id);

        await H.Bus.PublishAsync(new PaymentFailed(booking.Id, Guid.NewGuid(), "card_declined"));

        var failed = await WaitForStatusAsync(alice, booking.Id, BookingStatus.PaymentFailed);
        Assert.Contains("card_declined", failed.StatusReason);
        await H.Bus.WaitForAsync<BookingCancelled>(e => e.BookingId == booking.Id);
        var bob = await HoldAsync(H.ClientFor("bob"), eventId, [seats[0]]);
        Assert.Equal(HttpStatusCode.Created, bob.StatusCode);
    }

    [Fact]
    public async Task A_redelivered_payment_reply_is_applied_once()
    {
        var (eventId, seats) = await H.SeedEventAsync();
        var alice = H.ClientFor("alice");
        var booking = await ReadAsync(await HoldAsync(alice, eventId, [seats[0]]));
        await CheckoutAsync(alice, booking.Id);
        await H.Bus.WaitForAsync<ProcessPayment>(c => c.BookingId == booking.Id);

        var messageId = Guid.NewGuid();
        var reply = new PaymentSucceeded(booking.Id, Guid.NewGuid(), 100m, "TRY");
        await H.Bus.PublishAsync(reply, messageId);
        await H.Bus.PublishAsync(reply, messageId); // the broker delivered it twice

        await WaitForStatusAsync(alice, booking.Id, BookingStatus.Confirmed);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken); // give the duplicate time to be (not) processed

        Assert.Single(H.Bus.Received<BookingConfirmed>(e => e.BookingId == booking.Id));
        await using var scope = H.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        Assert.Equal(1, await db.Set<InboxMessage>().CountAsync(m => m.MessageId == messageId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_customer_can_cancel_a_hold_and_free_the_seats()
    {
        var (eventId, seats) = await H.SeedEventAsync();
        var alice = H.ClientFor("alice");
        var booking = await ReadAsync(await HoldAsync(alice, eventId, [seats[0]]));

        var cancel = await alice.DeleteAsync($"/api/bookings/{booking.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(BookingStatus.Cancelled, (await ReadAsync(cancel)).Status);
        Assert.Equal(SeatStatus.Available, (await H.SeatMapAsync(eventId)).Seats.Single(s => s.SeatId == seats[0]).Status);
    }
}

/// <summary>Saga timeouts with two-second deadlines.</summary>
public sealed class SagaTimeoutTests(ShortTimeoutBookingFixture fixture) : IClassFixture<ShortTimeoutBookingFixture>
{
    private BookingHarness H => fixture.Harness;

    [Fact]
    public async Task An_expired_hold_frees_the_seat_for_the_next_customer()
    {
        var (eventId, seats) = await H.SeedEventAsync();
        var alice = H.ClientFor("alice");
        var booking = await ReadAsync(await HoldAsync(alice, eventId, [seats[0]]));

        await WaitForStatusAsync(alice, booking.Id, BookingStatus.Expired, TimeSpan.FromSeconds(10));
        await H.Bus.WaitForAsync<BookingExpired>(e => e.BookingId == booking.Id);
        var bob = await HoldAsync(H.ClientFor("bob"), eventId, [seats[0]]);

        Assert.Equal(HttpStatusCode.Created, bob.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await CheckoutAsync(alice, booking.Id)).StatusCode);
    }

    [Fact]
    public async Task A_payment_that_lands_after_the_deadline_is_refunded()
    {
        var (eventId, seats) = await H.SeedEventAsync();
        var alice = H.ClientFor("alice");
        var booking = await ReadAsync(await HoldAsync(alice, eventId, [seats[0]]));
        await CheckoutAsync(alice, booking.Id);
        await H.Bus.WaitForAsync<ProcessPayment>(c => c.BookingId == booking.Id);

        await WaitForStatusAsync(alice, booking.Id, BookingStatus.Expired, TimeSpan.FromSeconds(10));
        var paymentId = Guid.NewGuid();
        await H.Bus.PublishAsync(new PaymentSucceeded(booking.Id, paymentId, 100m, "TRY")); // too late

        var refund = await H.Bus.WaitForAsync<RefundPayment>(r => r.BookingId == booking.Id);
        Assert.Equal(paymentId, refund.PaymentId);
        await WaitForStatusAsync(alice, booking.Id, BookingStatus.RefundPending);

        await H.Bus.PublishAsync(new PaymentRefunded(booking.Id, paymentId, 100m, "TRY"));

        await WaitForStatusAsync(alice, booking.Id, BookingStatus.Refunded);
        await H.Bus.WaitForAsync<BookingRefunded>(e => e.BookingId == booking.Id);
        Assert.Empty(H.Bus.Received<BookingConfirmed>(e => e.BookingId == booking.Id));
    }
}
