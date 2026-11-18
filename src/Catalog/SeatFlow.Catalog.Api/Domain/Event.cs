using SeatFlow.Contracts;

namespace SeatFlow.Catalog.Domain;

public sealed class Event
{
    private Event()
    {
    }

    public Guid Id { get; private set; }

    public Guid VenueId { get; private set; }

    public Venue Venue { get; private set; } = null!;

    public string Title { get; private set; } = "";

    public string? Description { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public string Currency { get; private set; } = "TRY";

    public List<SectionPrice> Prices { get; private set; } = [];

    public DateTimeOffset PublishedAt { get; private set; }

    public static Event Publish(
        Venue venue,
        string title,
        string? description,
        DateTimeOffset startsAt,
        string currency,
        IReadOnlyDictionary<string, decimal> prices,
        DateTimeOffset now)
    {
        if (startsAt <= now)
        {
            throw new ArgumentException("An event must start in the future.", nameof(startsAt));
        }

        var missing = venue.Sections.Select(s => s.Name).Where(name => !prices.ContainsKey(name)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"Missing price for section(s): {string.Join(", ", missing)}.", nameof(prices));
        }

        if (prices.Values.Any(p => p <= 0))
        {
            throw new ArgumentException("Prices must be positive.", nameof(prices));
        }

        return new Event
        {
            Id = Guid.CreateVersion7(),
            VenueId = venue.Id,
            Venue = venue,
            Title = title.Trim(),
            Description = description?.Trim(),
            StartsAt = startsAt.ToUniversalTime(),
            Currency = currency.ToUpperInvariant(),
            Prices = venue.Sections.Select(s => new SectionPrice { Section = s.Name, Price = prices[s.Name] }).ToList(),
            PublishedAt = now,
        };
    }

    public decimal PriceFor(string section) => Prices.Single(p => p.Section == section).Price;

    public EventPublished ToIntegrationEvent() => new(
        Id,
        Title,
        Venue.Name,
        Venue.City,
        StartsAt,
        Currency,
        Venue.Seats().Select(s => new PublishedSeat(s.SeatId, s.Section, s.Row, s.Number, PriceFor(s.Section))).ToList());
}

public sealed class SectionPrice
{
    public required string Section { get; init; }

    public decimal Price { get; init; }
}
