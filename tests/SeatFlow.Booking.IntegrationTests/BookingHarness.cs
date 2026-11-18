using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using SeatFlow.Bookings.Domain;
using SeatFlow.Bookings.Features;
using SeatFlow.Contracts;
using SeatFlow.Testing;

[assembly: AssemblyFixture(typeof(Infrastructure))]

namespace SeatFlow.Bookings.IntegrationTests;

/// <summary>
/// The real Booking service (in-memory HTTP via TestServer) wired to real containers. Each harness gets its own
/// database, queue and exchange, so test classes run in parallel without seeing each other's messages.
/// </summary>
public sealed class BookingHarness : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private BookingHarness(BookingApp app, TestBus bus)
    {
        App = app;
        Bus = bus;
    }

    public BookingApp App { get; }

    public TestBus Bus { get; }

    public static async Task<BookingHarness> StartAsync(Infrastructure infra, string prefix, IDictionary<string, string>? settings = null)
    {
        var app = new BookingApp(infra, $"{prefix}_{Guid.NewGuid():N}"[..20], settings ?? new Dictionary<string, string>());
        _ = app.Server; // boots the host: migrations run, consumers start
        var bus = await TestBus.StartAsync(infra.RabbitMq.GetConnectionString(), app.Exchange);
        await bus.WaitForConsumerAsync(app.Queue);
        return new BookingHarness(app, bus);
    }

    public HttpClient ClientFor(string userId) => App.CreateClient().As(userId);

    public HttpClient Anonymous() => App.CreateClient();

    /// <summary>Publishes a catalog event the way Catalog would and waits until Booking's read model has it.</summary>
    public async Task<(Guid EventId, string[] Seats)> SeedEventAsync(int seats = 6, decimal price = 100m)
    {
        var eventId = Guid.NewGuid();
        var published = Enumerable.Range(1, seats).Select(n => new PublishedSeat($"A-1-{n}", "A", 1, n, price)).ToList();
        await Bus.PublishAsync(new EventPublished(eventId, "Test Concert", "Test Hall", "Istanbul", DateTimeOffset.UtcNow.AddDays(10), "TRY", published));

        var anonymous = Anonymous();
        await Eventually.TrueAsync(
            async () => (await anonymous.GetAsync($"/api/bookings/events/{eventId}/seats")).IsSuccessStatusCode,
            because: "the event to reach Booking's read model");
        return (eventId, published.Select(s => s.SeatId).ToArray());
    }

    public static Task<HttpResponseMessage> HoldAsync(HttpClient client, Guid eventId, string[] seats, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings")
        {
            Content = JsonContent.Create(new { eventId, seatIds = seats }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> CheckoutAsync(HttpClient client, Guid bookingId, string token = "tok_visa") =>
        client.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout", new { paymentToken = token });

    public static async Task<BookingResponse> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<BookingResponse>(Json))!;

    public static async Task<BookingResponse> GetAsync(HttpClient client, Guid bookingId) =>
        (await client.GetFromJsonAsync<BookingResponse>($"/api/bookings/{bookingId}", Json))!;

    public static Task<BookingResponse> WaitForStatusAsync(HttpClient client, Guid bookingId, BookingStatus status, TimeSpan? timeout = null) =>
        Eventually.GetAsync(async () =>
        {
            var booking = await GetAsync(client, bookingId);
            return booking.Status == status ? booking : null;
        }, timeout, $"booking {bookingId} to become {status}");

    public async Task<EventAvailability> SeatMapAsync(Guid eventId) =>
        (await Anonymous().GetFromJsonAsync<EventAvailability>($"/api/bookings/events/{eventId}/seats", Json))!;

    public async ValueTask DisposeAsync()
    {
        await Bus.DisposeAsync();
        await App.DisposeAsync();
    }
}

public sealed class BookingApp(Infrastructure infra, string name, IDictionary<string, string> settings) : WebApplicationFactory<Program>
{
    public string Exchange { get; } = $"seatflow.{name}";

    public string Queue { get; } = $"booking.{name}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", infra.PostgresConnectionString($"booking_{name}"));
        builder.UseSetting("ConnectionStrings:Redis", infra.Redis.GetConnectionString());
        builder.UseSetting("ConnectionStrings:RabbitMq", infra.RabbitMq.GetConnectionString());
        builder.UseSetting("RabbitMq:Exchange", Exchange);
        builder.UseSetting("RabbitMq:DeadLetterExchange", $"{Exchange}.dlx");
        builder.UseSetting("RabbitMq:QueueName", Queue);
        builder.UseSetting("RabbitMq:OutboxPollInterval", "00:00:00.200");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Booking:ExpiryScanInterval", "00:00:00.300");
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(TestAuth.UseTestSigningKey);
    }
}

public sealed class DefaultBookingFixture(Infrastructure infra) : IAsyncLifetime
{
    public BookingHarness Harness { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Harness = await BookingHarness.StartAsync(infra, "default");

    public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
}

/// <summary>Two-second holds and payment deadlines, to exercise the saga's timeouts.</summary>
public sealed class ShortTimeoutBookingFixture(Infrastructure infra) : IAsyncLifetime
{
    public BookingHarness Harness { get; private set; } = null!;

    public async ValueTask InitializeAsync() =>
        Harness = await BookingHarness.StartAsync(infra, "timeouts", new Dictionary<string, string>
        {
            ["Booking:HoldTtl"] = "00:00:02",
            ["Booking:PaymentTimeout"] = "00:00:02",
        });

    public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
}
