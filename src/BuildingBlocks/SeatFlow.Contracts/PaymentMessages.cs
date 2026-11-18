namespace SeatFlow.Contracts;

// Commands sent by the booking saga to Payment.

[Message("payment.process", Required = true)]
public sealed record ProcessPayment(
    Guid BookingId,
    string UserId,
    decimal Amount,
    string Currency,
    string PaymentToken);

/// <summary>Compensating command: the booking could not be confirmed after the customer was charged.</summary>
[Message("payment.refund", Required = true)]
public sealed record RefundPayment(Guid BookingId, Guid PaymentId, string Reason);

// Replies from Payment back to the saga.

[Message("payment.succeeded", Required = true)]
public sealed record PaymentSucceeded(Guid BookingId, Guid PaymentId, decimal Amount, string Currency);

[Message("payment.failed", Required = true)]
public sealed record PaymentFailed(Guid BookingId, Guid PaymentId, string Reason);

[Message("payment.refunded", Required = true)]
public sealed record PaymentRefunded(Guid BookingId, Guid PaymentId, decimal Amount, string Currency);
