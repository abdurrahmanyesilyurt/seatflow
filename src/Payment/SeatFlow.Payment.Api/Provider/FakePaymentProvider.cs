using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace SeatFlow.Payments.Provider;

public sealed record ChargeRequest(string IdempotencyKey, decimal Amount, string Currency, string Token);

public sealed record ChargeResult(bool Succeeded, string? ProviderReference, string? FailureReason);

/// <summary>The port to a card processor. Real providers (Stripe, iyzico, Adyen) all accept an idempotency key.</summary>
public interface IPaymentProvider
{
    Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken cancellationToken);

    Task RefundAsync(string providerReference, decimal amount, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed class FakePaymentProviderOptions
{
    public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How long <c>tok_slow</c> takes. Longer than the booking's payment timeout, it triggers the refund path.</summary>
    public TimeSpan SlowDelay { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Deterministic stand-in for a card processor, driven by test tokens:
/// <c>tok_visa</c>/<c>tok_mastercard</c> succeed, <c>tok_declined</c> and <c>tok_insufficient_funds</c> fail,
/// <c>tok_slow</c> succeeds after <see cref="FakePaymentProviderOptions.SlowDelay"/>. Charges are idempotent per key,
/// like a real provider, so a redelivered command never charges twice.
/// </summary>
public sealed partial class FakePaymentProvider(IOptions<FakePaymentProviderOptions> options, ILogger<FakePaymentProvider> logger)
    : IPaymentProvider
{
    private readonly ConcurrentDictionary<string, ChargeResult> _charges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _refunds = new(StringComparer.Ordinal);

    public async Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken cancellationToken)
    {
        if (_charges.TryGetValue(request.IdempotencyKey, out var previous))
        {
            return previous;
        }

        await Task.Delay(options.Value.Latency, cancellationToken);

        var result = request.Token switch
        {
            "tok_visa" or "tok_mastercard" => Approved(),
            "tok_slow" => await SlowApprovalAsync(cancellationToken),
            "tok_declined" => new ChargeResult(false, null, "card_declined"),
            "tok_insufficient_funds" => new ChargeResult(false, null, "insufficient_funds"),
            _ => new ChargeResult(false, null, "invalid_token"),
        };

        LogCharge(request.IdempotencyKey, request.Amount, request.Currency, result.Succeeded, result.FailureReason);
        return _charges.GetOrAdd(request.IdempotencyKey, result);
    }

    public async Task RefundAsync(string providerReference, decimal amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (_refunds.ContainsKey(idempotencyKey))
        {
            return;
        }

        await Task.Delay(options.Value.Latency, cancellationToken);
        _refunds.TryAdd(idempotencyKey, true);
        LogRefund(providerReference, amount);
    }

    private async Task<ChargeResult> SlowApprovalAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(options.Value.SlowDelay, cancellationToken);
        return Approved();
    }

    private static ChargeResult Approved() => new(true, $"ch_{Guid.NewGuid():N}", null);

    [LoggerMessage(Level = LogLevel.Information, Message = "Charge {Key}: {Amount} {Currency} -> succeeded={Succeeded} {Reason}")]
    private partial void LogCharge(string key, decimal amount, string currency, bool succeeded, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refunded {Reference}: {Amount}")]
    private partial void LogRefund(string reference, decimal amount);
}
