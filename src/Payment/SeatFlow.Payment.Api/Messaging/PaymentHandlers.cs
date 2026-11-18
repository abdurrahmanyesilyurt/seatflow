using Microsoft.EntityFrameworkCore;
using SeatFlow.Contracts;
using SeatFlow.Messaging.Consuming;
using SeatFlow.Messaging.Outbox;
using SeatFlow.Payments.Data;
using SeatFlow.Payments.Domain;
using SeatFlow.Payments.Provider;

namespace SeatFlow.Payments.Messaging;

/// <summary>
/// Charges the customer for a booking. Three things stop a double charge: the inbox (same message twice), the unique
/// booking_id (two different commands for one booking) and the provider idempotency key (crash between charge and commit).
/// </summary>
internal sealed class ProcessPaymentHandler(
    PaymentDbContext db,
    IPaymentProvider provider,
    IOutbox outbox,
    TimeProvider time) : IMessageHandler<ProcessPayment>
{
    public async Task HandleAsync(ProcessPayment message, MessageContext context, CancellationToken cancellationToken)
    {
        var existing = await db.Payments.SingleOrDefaultAsync(p => p.BookingId == message.BookingId, cancellationToken);
        if (existing is not null)
        {
            outbox.Enqueue(PaymentReplies.For(existing), message.BookingId.ToString()); // answer again, never charge again
            return;
        }

        var result = await provider.ChargeAsync(
            new ChargeRequest($"booking:{message.BookingId}", message.Amount, message.Currency, message.PaymentToken),
            cancellationToken);

        var now = time.GetUtcNow();
        var payment = result.Succeeded
            ? Payment.Captured(message.BookingId, message.UserId, message.Amount, message.Currency, result.ProviderReference!, now)
            : Payment.Declined(message.BookingId, message.UserId, message.Amount, message.Currency, result.FailureReason ?? "declined", now);

        db.Payments.Add(payment);
        outbox.Enqueue(PaymentReplies.For(payment), message.BookingId.ToString());
    }
}

/// <summary>Compensating action of the booking saga.</summary>
internal sealed class RefundPaymentHandler(
    PaymentDbContext db,
    IPaymentProvider provider,
    IOutbox outbox,
    TimeProvider time) : IMessageHandler<RefundPayment>
{
    public async Task HandleAsync(RefundPayment message, MessageContext context, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == message.PaymentId, cancellationToken)
            ?? throw new InvalidOperationException($"Refund requested for unknown payment {message.PaymentId}.");

        if (payment.Status == PaymentStatus.Succeeded)
        {
            await provider.RefundAsync(payment.ProviderReference!, payment.Amount, $"refund:{payment.Id}", cancellationToken);
            payment.MarkRefunded(message.Reason, time.GetUtcNow());
        }

        outbox.Enqueue(PaymentReplies.For(payment), message.BookingId.ToString());
    }
}

internal static class PaymentReplies
{
    public static object For(Payment payment) => payment.Status switch
    {
        PaymentStatus.Succeeded => new PaymentSucceeded(payment.BookingId, payment.Id, payment.Amount, payment.Currency),
        PaymentStatus.Failed => new PaymentFailed(payment.BookingId, payment.Id, payment.FailureReason ?? "declined"),
        PaymentStatus.Refunded => new PaymentRefunded(payment.BookingId, payment.Id, payment.Amount, payment.Currency),
        _ => throw new InvalidOperationException($"Unexpected payment status {payment.Status}."),
    };
}
