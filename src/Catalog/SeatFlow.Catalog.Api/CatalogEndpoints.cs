using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using SeatFlow.Catalog.Data;
using SeatFlow.Catalog.Domain;
using SeatFlow.Messaging.Outbox;
using SeatFlow.ServiceDefaults;

namespace SeatFlow.Catalog;

public sealed record EventSummary(
    Guid Id, string Title, string Venue, string City, DateTimeOffset StartsAt, decimal PriceFrom, string Currency);

public sealed record EventDetails(
    Guid Id, string Title, string? Description, DateTimeOffset StartsAt, string Currency, VenueDto Venue,
    IReadOnlyList<SectionDto> Sections);

public sealed record VenueDto(Guid Id, string Name, string City, int Capacity);

public sealed record SectionDto(string Name, int Rows, int SeatsPerRow, decimal Price);

public sealed record CreateVenueRequest(string Name, string City, IReadOnlyList<CreateSectionRequest> Sections);

public sealed record CreateSectionRequest(string Name, int Rows, int SeatsPerRow);

public sealed record CreateEventRequest(
    Guid VenueId, string Title, string? Description, DateTimeOffset StartsAt, string Currency,
    IReadOnlyDictionary<string, decimal> Prices);

public static class CatalogEndpoints
{
    private const string EventsTag = "events";

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog").WithTags("Catalog");

        group.MapGet("/events", ListEventsAsync);
        group.MapGet("/events/{id:guid}", GetEventAsync).WithName("GetEvent");
        group.MapPost("/events", CreateEventAsync).RequireAuthorization(SeatFlowAuth.AdminPolicy);
        group.MapGet("/venues", ListVenuesAsync);
        group.MapPost("/venues", CreateVenueAsync).RequireAuthorization(SeatFlowAuth.AdminPolicy);

        return app;
    }

    private static async Task<IReadOnlyList<EventSummary>> ListEventsAsync(
        HybridCache cache, CatalogDbContext db, TimeProvider time, CancellationToken ct) =>
        await cache.GetOrCreateAsync(
            "events:upcoming",
            async token =>
            {
                var now = time.GetUtcNow();
                var events = await db.Events.AsNoTracking().Include(e => e.Venue)
                    .Where(e => e.StartsAt > now)
                    .OrderBy(e => e.StartsAt)
                    .ToListAsync(token);
                return events
                    .Select(e => new EventSummary(e.Id, e.Title, e.Venue.Name, e.Venue.City, e.StartsAt, e.Prices.Min(p => p.Price), e.Currency))
                    .ToList();
            },
            tags: [EventsTag],
            cancellationToken: ct);

    private static async Task<IResult> GetEventAsync(Guid id, HybridCache cache, CatalogDbContext db, CancellationToken ct)
    {
        var details = await cache.GetOrCreateAsync(
            $"events:{id}",
            async token =>
            {
                var e = await db.Events.AsNoTracking().Include(x => x.Venue).SingleOrDefaultAsync(x => x.Id == id, token);
                return e is null ? null : ToDetails(e);
            },
            tags: [EventsTag],
            cancellationToken: ct);

        return details is null ? TypedResults.NotFound() : TypedResults.Ok(details);
    }

    private static async Task<IResult> CreateEventAsync(
        CreateEventRequest request, CatalogDbContext db, IOutbox outbox, HybridCache cache, TimeProvider time, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Currency is not { Length: 3 })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["Title is required and Currency must be a 3-letter ISO code."],
            });
        }

        var venue = await db.Venues.SingleOrDefaultAsync(v => v.Id == request.VenueId, ct);
        if (venue is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["venueId"] = ["Venue not found."] });
        }

        Event created;
        try
        {
            created = Event.Publish(venue, request.Title, request.Description, request.StartsAt, request.Currency, request.Prices, time.GetUtcNow());
        }
        catch (ArgumentException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
        }

        db.Events.Add(created);
        outbox.Enqueue(created.ToIntegrationEvent(), correlationId: created.Id.ToString());
        await db.SaveChangesAsync(ct); // event row + outbox row in one transaction

        await cache.RemoveByTagAsync(EventsTag, ct);
        return TypedResults.CreatedAtRoute(ToDetails(created), "GetEvent", new { id = created.Id });
    }

    private static async Task<IReadOnlyList<VenueDto>> ListVenuesAsync(CatalogDbContext db, CancellationToken ct)
    {
        var venues = await db.Venues.AsNoTracking().OrderBy(v => v.Name).ToListAsync(ct);
        return venues.Select(v => new VenueDto(v.Id, v.Name, v.City, v.Capacity)).ToList();
    }

    private static async Task<IResult> CreateVenueAsync(CreateVenueRequest request, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Sections is not { Count: > 0 }
            || request.Sections.Any(s => string.IsNullOrWhiteSpace(s.Name) || s.Rows is < 1 or > 100 || s.SeatsPerRow is < 1 or > 200))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["Name and at least one section (1-100 rows, 1-200 seats per row) are required."],
            });
        }

        if (await db.Venues.AnyAsync(v => v.Name == request.Name.Trim(), ct))
        {
            return TypedResults.Conflict(new { error = "A venue with this name already exists." });
        }

        Venue venue;
        try
        {
            venue = Venue.Create(request.Name, request.City,
                request.Sections.Select(s => new VenueSection { Name = s.Name.Trim(), Rows = s.Rows, SeatsPerRow = s.SeatsPerRow }));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
        }

        db.Venues.Add(venue);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/catalog/venues/{venue.Id}", new VenueDto(venue.Id, venue.Name, venue.City, venue.Capacity));
    }

    private static EventDetails ToDetails(Event e) => new(
        e.Id,
        e.Title,
        e.Description,
        e.StartsAt,
        e.Currency,
        new VenueDto(e.Venue.Id, e.Venue.Name, e.Venue.City, e.Venue.Capacity),
        e.Venue.Sections.Select(s => new SectionDto(s.Name, s.Rows, s.SeatsPerRow, e.PriceFor(s.Name))).ToList());
}
