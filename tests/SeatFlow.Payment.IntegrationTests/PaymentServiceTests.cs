using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeatFlow.Contracts;
using SeatFlow.Payments.Data;
using SeatFlow.Payments.Domain;
using SeatFlow.Testing;

[assembly: AssemblyFixture(typeof(Infrastructure))]

namespace SeatFlow.Payments.IntegrationTests;

/// <summary>The real Payment service on real PostgreSQL and RabbitMQ; the test bus plays the booking saga.</summary>
public sealed class PaymentFixture(Infrastructure infra) : IAsyncLifetime
{
    public PaymentApp App { get; private set; } = null!;

    public TestBus Bus { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        App = new PaymentApp(infra, $"pay_{Guid.NewGuid():N}"[..16]);
        _ = App.Server;
        Bus = await TestBus.StartAsync(infra.RabbitMq.GetConnectionString(), App.Exchange);
        await Bus.WaitForConsumerAsync(App.Queue);
    }

    public async ValueTask DisposeAsync()
    {
        await Bus.DisposeAsync();
        await App.DisposeAsync();
    }
}

public sealed class PaymentApp(Infrastructure infra, string name) : WebApplicationFactory<Program>
{
    public string Exchange { get; } = $"seatflow.{name}";

    public string Queue { get; } = $"payment.{name}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", infra.PostgresConnectionString(name));
        builder.UseSetting("ConnectionStrings:RabbitMq", infra.RabbitMq.GetConnectionString());
        builder.UseSetting("RabbitMq:Exchange", Exchange);
        builder.UseSetting("RabbitMq:DeadLetterExchange", $"{Exchange}.dlx");
        builder.UseSetting("RabbitMq:QueueName", Queue);
        builder.UseSetting("RabbitMq:OutboxPollInterval", "00:00:00.200");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("FakeProvider:Latency", "00:00:00.010");
        builder.ConfigureTestServices(TestAuth.UseTestSigningKey);
    }
}

public sealed class PaymentServiceTests(PaymentFixture fixture) : IClassFixture<PaymentFixture>
{
    private async Task<List<Payment>> PaymentsForAsync(Guid bookingId)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return await db.Payments.AsNoTracking().Where(p => p.BookingId == bookingId).ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_valid_card_is_charged_and_the_saga_is_told()
    {
        var bookingId = Guid.NewGuid();

        await fixture.Bus.PublishAsync(new ProcessPayment(bookingId, "alice", 750m, "TRY", "tok_visa"));

        var reply = await fixture.Bus.WaitForAsync<PaymentSucceeded>(r => r.BookingId == bookingId);
        Assert.Equal(750m, reply.Amount);
        var payment = Assert.Single(await PaymentsForAsync(bookingId));
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(reply.PaymentId, payment.Id);
    }

    [Fact]
    public async Task A_declined_card_is_reported_as_a_failure()
    {
        var bookingId = Guid.NewGuid();

        await fixture.Bus.PublishAsync(new ProcessPayment(bookingId, "alice", 100m, "TRY", "tok_declined"));

        var reply = await fixture.Bus.WaitForAsync<PaymentFailed>(r => r.BookingId == bookingId);
        Assert.Equal("card_declined", reply.Reason);
        Assert.Equal(PaymentStatus.Failed, Assert.Single(await PaymentsForAsync(bookingId)).Status);
    }

    [Fact]
    public async Task A_second_command_for_the_same_booking_never_charges_twice()
    {
        var bookingId = Guid.NewGuid();
        var command = new ProcessPayment(bookingId, "alice", 100m, "TRY", "tok_visa");

        await fixture.Bus.PublishAsync(command); // two distinct messages (different ids),
        await fixture.Bus.PublishAsync(command); // e.g. the saga re-sent after a timeout

        await Eventually.TrueAsync(
            () => Task.FromResult(fixture.Bus.Received<PaymentSucceeded>(r => r.BookingId == bookingId).Count == 2),
            because: "both commands to be answered");
        var replies = fixture.Bus.Received<PaymentSucceeded>(r => r.BookingId == bookingId);
        Assert.Single(replies.Select(r => r.PaymentId).Distinct()); // same payment both times
        Assert.Single(await PaymentsForAsync(bookingId));
    }

    [Fact]
    public async Task A_refund_compensates_a_captured_payment()
    {
        var bookingId = Guid.NewGuid();
        await fixture.Bus.PublishAsync(new ProcessPayment(bookingId, "alice", 300m, "TRY", "tok_visa"));
        var captured = await fixture.Bus.WaitForAsync<PaymentSucceeded>(r => r.BookingId == bookingId);

        await fixture.Bus.PublishAsync(new RefundPayment(bookingId, captured.PaymentId, "Booking expired before payment completed"));

        var refunded = await fixture.Bus.WaitForAsync<PaymentRefunded>(r => r.BookingId == bookingId);
        Assert.Equal(300m, refunded.Amount);
        var payment = Assert.Single(await PaymentsForAsync(bookingId));
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.NotNull(payment.RefundedAt);
    }

    [Fact]
    public async Task Customers_only_see_their_own_payments()
    {
        var bookingId = Guid.NewGuid();
        await fixture.Bus.PublishAsync(new ProcessPayment(bookingId, "alice", 100m, "TRY", "tok_visa"));
        await fixture.Bus.WaitForAsync<PaymentSucceeded>(r => r.BookingId == bookingId);

        var mine = await fixture.App.CreateClient().As("alice").GetAsync($"/api/payments/{bookingId}", TestContext.Current.CancellationToken);
        var theirs = await fixture.App.CreateClient().As("mallory").GetAsync($"/api/payments/{bookingId}", TestContext.Current.CancellationToken);
        var anonymous = await fixture.App.CreateClient().GetAsync($"/api/payments/{bookingId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(100m, (await mine.Content.ReadFromJsonAsync<PaymentView>(TestContext.Current.CancellationToken))!.Amount);
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private sealed record PaymentView(Guid Id, decimal Amount, string Currency);
}
