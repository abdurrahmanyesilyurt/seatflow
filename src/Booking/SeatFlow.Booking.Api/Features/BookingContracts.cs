using SeatFlow.Bookings.Domain;

namespace SeatFlow.Bookings.Features;

public sealed record HoldSeatsRequest(Guid EventId, IReadOnlyList<string> SeatIds);

public sealed record CheckoutRequest(string PaymentToken);

public sealed record SeatLine(string SeatId, decimal Price);

public sealed record BookingResponse(
    Guid Id,
    Guid EventId,
    string EventTitle,
    BookingStatus Status,
    IReadOnlyList<SeatLine> Seats,
    decimal Total,
    string Currency,
    DateTimeOffset CreatedAt,
    DateTimeOffset HoldExpiresAt,
    DateTimeOffset? PaymentDeadline,
    string? StatusReason)
{
    public static BookingResponse From(Booking b) => new(
        b.Id,
        b.EventId,
        b.EventTitle,
        b.Status,
        b.Seats.Select(s => new SeatLine(s.SeatId, s.Price)).ToList(),
        b.TotalAmount,
        b.Currency,
        b.CreatedAt,
        b.HoldExpiresAt,
        b.PaymentDeadline,
        b.StatusReason);
}

public enum SeatStatus
{
    Available,
    Held,
    Sold,
}

public sealed record SeatAvailability(string SeatId, string Section, int Row, int Number, decimal Price, SeatStatus Status);

public sealed record EventAvailability(
    Guid EventId,
    string Title,
    DateTimeOffset StartsAt,
    string Currency,
    int Available,
    int Held,
    int Sold,
    IReadOnlyList<SeatAvailability> Seats);

internal static class BookingProblems
{
    public static IResult SeatsUnavailable(IEnumerable<string> seats) => TypedResults.Problem(
        title: "Seats unavailable",
        detail: "One or more seats are held or sold by someone else.",
        statusCode: StatusCodes.Status409Conflict,
        extensions: new Dictionary<string, object?> { ["seats"] = seats.Distinct().Order().ToArray() });

    public static IResult InvalidState(string detail) => TypedResults.Problem(
        title: "Invalid booking state",
        detail: detail,
        statusCode: StatusCodes.Status409Conflict);

    public static IResult Validation(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
