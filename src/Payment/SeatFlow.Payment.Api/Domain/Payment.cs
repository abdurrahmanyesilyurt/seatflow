namespace SeatFlow.Payments.Domain;

public enum PaymentStatus
{
    Succeeded,
    Failed,
    Refunded,
}

public sealed class Payment
{
    private Payment()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Unique: one payment per booking, however many times the command is delivered.</summary>
    public Guid BookingId { get; private set; }

    public string UserId { get; private set; } = "";

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "";

    public PaymentStatus Status { get; private set; }

    public string? ProviderReference { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RefundedAt { get; private set; }

    public string? RefundReason { get; private set; }

    public uint Version { get; private set; }

    public static Payment Captured(Guid bookingId, string userId, decimal amount, string currency, string providerReference, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            BookingId = bookingId,
            UserId = userId,
            Amount = amount,
            Currency = currency,
            Status = PaymentStatus.Succeeded,
            ProviderReference = providerReference,
            CreatedAt = now,
        };

    public static Payment Declined(Guid bookingId, string userId, decimal amount, string currency, string reason, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            BookingId = bookingId,
            UserId = userId,
            Amount = amount,
            Currency = currency,
            Status = PaymentStatus.Failed,
            FailureReason = reason,
            CreatedAt = now,
        };

    public void MarkRefunded(string reason, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Succeeded)
        {
            throw new InvalidOperationException($"Cannot refund a payment that is {Status}.");
        }

        Status = PaymentStatus.Refunded;
        RefundReason = reason;
        RefundedAt = now;
    }
}
