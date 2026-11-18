namespace SeatFlow.Bookings.Domain;

/// <summary>Booking's local copy of a catalog event, kept up to date from <c>catalog.event-published</c>.</summary>
public sealed class EventInfo
{
    public Guid Id { get; set; }

    public string Title { get; set; } = "";

    public string VenueName { get; set; } = "";

    public string City { get; set; } = "";

    public DateTimeOffset StartsAt { get; set; }

    public string Currency { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EventSeat
{
    public Guid EventId { get; set; }

    public string SeatId { get; set; } = "";

    public string Section { get; set; } = "";

    public int Row { get; set; }

    public int Number { get; set; }

    public decimal Price { get; set; }
}
