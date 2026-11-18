namespace SeatFlow.Catalog.Domain;

public sealed class Venue
{
    private Venue()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = "";

    public string City { get; private set; } = "";

    public List<VenueSection> Sections { get; private set; } = [];

    public int Capacity => Sections.Sum(s => s.Rows * s.SeatsPerRow);

    public static Venue Create(string name, string city, IEnumerable<VenueSection> sections)
    {
        var list = sections.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("A venue needs at least one section.", nameof(sections));
        }

        if (list.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count)
        {
            throw new ArgumentException("Section names must be unique.", nameof(sections));
        }

        return new Venue { Id = Guid.CreateVersion7(), Name = name.Trim(), City = city.Trim(), Sections = list };
    }

    public IEnumerable<SeatPosition> Seats() =>
        from section in Sections
        from row in Enumerable.Range(1, section.Rows)
        from number in Enumerable.Range(1, section.SeatsPerRow)
        select new SeatPosition($"{section.Name}-{row}-{number}", section.Name, row, number);
}

public sealed class VenueSection
{
    public required string Name { get; init; }

    public int Rows { get; init; }

    public int SeatsPerRow { get; init; }
}

public sealed record SeatPosition(string SeatId, string Section, int Row, int Number);
