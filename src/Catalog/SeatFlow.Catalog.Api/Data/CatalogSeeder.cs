using Microsoft.EntityFrameworkCore;
using Npgsql;
using SeatFlow.Catalog.Domain;
using SeatFlow.Messaging.Outbox;

namespace SeatFlow.Catalog.Data;

/// <summary>Seeds one venue and two events on an empty database. Events go through the outbox like any other publish.</summary>
public static class CatalogSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CatalogSeeder));

        if (await db.Venues.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = time.GetUtcNow();
        var evening = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(17); // 20:00 in Istanbul

        var arena = Venue.Create("SeatFlow Arena", "Istanbul",
        [
            new VenueSection { Name = "VIP", Rows = 2, SeatsPerRow = 10 },
            new VenueSection { Name = "A", Rows = 5, SeatsPerRow = 20 },
            new VenueSection { Name = "B", Rows = 5, SeatsPerRow = 20 },
        ]);

        Event[] events =
        [
            Event.Publish(arena, "Jazz Under the Stars", "An open-air jazz evening.", evening.AddDays(30), "TRY",
                new Dictionary<string, decimal> { ["VIP"] = 2500m, ["A"] = 1200m, ["B"] = 750m }, now),
            Event.Publish(arena, "Indie Rock Night", "Three bands, one stage.", evening.AddDays(45), "TRY",
                new Dictionary<string, decimal> { ["VIP"] = 1800m, ["A"] = 900m, ["B"] = 500m }, now),
        ];

        db.Venues.Add(arena);
        db.Events.AddRange(events);
        foreach (var e in events)
        {
            outbox.Enqueue(e.ToIntegrationEvent(), correlationId: e.Id.ToString());
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded venue {Venue} with {Count} events", arena.Name, events.Length);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            logger.LogInformation("Another replica seeded the catalog first");
        }
    }
}
