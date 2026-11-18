namespace SeatFlow.Contracts;

/// <summary>
/// Event-carried state transfer: Booking keeps its own copy of events, seats and prices, so holding a
/// seat never needs a synchronous call to Catalog.
/// </summary>
[Message("catalog.event-published", Required = true)]
public sealed record EventPublished(
    Guid EventId,
    string Title,
    string VenueName,
    string City,
    DateTimeOffset StartsAt,
    string Currency,
    IReadOnlyList<PublishedSeat> Seats);

public sealed record PublishedSeat(string SeatId, string Section, int Row, int Number, decimal Price);
