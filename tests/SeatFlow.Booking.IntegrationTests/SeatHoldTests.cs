using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SeatFlow.Bookings.Seats;
using SeatFlow.Testing;
using StackExchange.Redis;
using static SeatFlow.Bookings.IntegrationTests.BookingHarness;

namespace SeatFlow.Bookings.IntegrationTests;

public sealed class SeatHoldTests(DefaultBookingFixture fixture) : IClassFixture<DefaultBookingFixture>
{
    private BookingHarness H => fixture.Harness;

    [Fact]
    public async Task A_retry_with_the_same_idempotency_key_replays_the_original_booking()
    {
        var (eventId, seats) = await H.SeedEventAsync();
        var alice = H.ClientFor("alice");
        var key = Guid.NewGuid().ToString();

        var first = await HoldAsync(alice, eventId, seats[..2], key);
        var second = await HoldAsync(alice, eventId, seats[..2], key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        var booking = await ReadAsync(first);
        Assert.Equal(booking.Id, (await ReadAsync(second)).Id);
        Assert.Equal(200m, booking.Total);
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_for_different_seats_is_rejected()
    {
        var (eventId, seats) = await H.SeedEventAsync();
        var alice = H.ClientFor("alice");
        var key = Guid.NewGuid().ToString();

        await HoldAsync(alice, eventId, [seats[0]], key);
        var reused = await HoldAsync(alice, eventId, [seats[1]], key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
    }

    [Fact]
    public async Task Concurrent_retries_of_one_request_create_exactly_one_booking()
    {
        var (eventId, seats) = await H.SeedEventAsync();
        var alice = H.ClientFor("alice");
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => HoldAsync(alice, eventId, seats[..2], key)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        var ids = await Task.WhenAll(responses.Select(ReadAsync));
        Assert.Single(ids.Select(b => b.Id).Distinct());
    }

    [Fact]
    public async Task Only_one_of_many_concurrent_customers_gets_a_contested_seat()
    {
        var (eventId, seats) = await H.SeedEventAsync();
        const int customers = 25;

        var responses = await Task.WhenAll(Enumerable.Range(0, customers)
            .Select(i => HoldAsync(H.ClientFor($"racer-{i}"), eventId, [seats[0]])));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(customers - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var map = await H.SeatMapAsync(eventId);
        Assert.Equal(1, map.Held);
    }

    [Fact]
    public async Task Overlapping_multi_seat_requests_are_all_or_nothing()
    {
        var (eventId, seats) = await H.SeedEventAsync();

        // Each request overlaps its neighbours: [1,2], [2,3], [3,4], [4,5] ... nobody may end up with half a request.
        var requests = Enumerable.Range(0, seats.Length - 1)
            .Select(i => HoldAsync(H.ClientFor($"pair-{i}"), eventId, [seats[i], seats[i + 1]]));
        var responses = await Task.WhenAll(requests);

        var winners = await Task.WhenAll(responses.Where(r => r.StatusCode == HttpStatusCode.Created).Select(ReadAsync));
        var heldSeats = winners.SelectMany(b => b.Seats.Select(s => s.SeatId)).ToList();
        Assert.NotEmpty(winners);
        Assert.Equal(heldSeats.Count, heldSeats.Distinct().Count()); // no seat in two bookings
        Assert.All(winners, b => Assert.Equal(2, b.Seats.Count));    // and nobody got half
        Assert.Equal(heldSeats.Count, (await H.SeatMapAsync(eventId)).Held);
    }

    [Fact]
    public async Task The_database_still_protects_a_seat_when_its_redis_lock_vanishes()
    {
        var (eventId, seats) = await H.SeedEventAsync();
        var alice = await ReadAsync(await HoldAsync(H.ClientFor("alice"), eventId, [seats[0]]));

        // Simulate a Redis failover that lost the lock.
        var redis = H.App.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        Assert.True(await redis.KeyDeleteAsync(SeatLockService.Key(eventId, seats[0])));

        var bob = await HoldAsync(H.ClientFor("bob"), eventId, [seats[0]]);

        Assert.Equal(HttpStatusCode.Conflict, bob.StatusCode);
        Assert.Equal(alice.Id, (await GetAsync(H.ClientFor("alice"), alice.Id)).Id);
    }

    [Fact]
    public async Task Requests_are_validated_and_authenticated()
    {
        var (eventId, seats) = await H.SeedEventAsync();

        var anonymous = await HoldAsync(H.Anonymous(), eventId, [seats[0]]);
        var unknownSeat = await HoldAsync(H.ClientFor("alice"), eventId, ["Z-9-99"]);
        var duplicateSeat = await HoldAsync(H.ClientFor("alice"), eventId, [seats[0], seats[0]]);
        var unknownEvent = await HoldAsync(H.ClientFor("alice"), Guid.NewGuid(), [seats[0]]);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownSeat.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateSeat.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownEvent.StatusCode);
    }

    [Fact]
    public async Task Customers_cannot_see_each_others_bookings()
    {
        var (eventId, seats) = await H.SeedEventAsync();
        var booking = await ReadAsync(await HoldAsync(H.ClientFor("alice"), eventId, [seats[0]]));

        var asBob = await H.ClientFor("bob").GetAsync($"/api/bookings/{booking.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, asBob.StatusCode);
    }
}
