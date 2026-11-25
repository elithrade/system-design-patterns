using System.Net;
using System.Net.Http.Json;

namespace Realtime.Tests.Baseline;

// Guards: bidding and polling work. These pass from the start.
[Collection("infra")]
public sealed class PollingTests(InfraFixture infra)
{
    [Fact]
    public async Task A_poll_returns_the_bids_after_an_id()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        var first = await client.PlaceBidAsync(1, 110);
        var second = await client.PlaceBidAsync(1, 120);
        await client.PlaceBidAsync(2, 130);

        var all = await client.GetFromJsonAsync<List<BidEvent>>("/auctions/1/bids?after=0", Json.Options);
        var later = await client.GetFromJsonAsync<List<BidEvent>>($"/auctions/1/bids?after={first.Id}", Json.Options);

        Assert.Equal([first.Id, second.Id], all!.Select(b => b.Id));
        Assert.Equal([second.Id], later!.Select(b => b.Id));
    }

    [Fact]
    public async Task A_bid_must_beat_the_current_price()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        await client.PlaceBidAsync(1, 150);

        var low = await client.PostAsJsonAsync("/auctions/1/bids", new PlaceBid(2, 150));
        var missing = await client.PostAsJsonAsync("/auctions/999/bids", new PlaceBid(2, 500));

        Assert.Equal(HttpStatusCode.Conflict, low.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(150, (await client.GetFromJsonAsync<Auction>("/auctions/1"))!.CurrentPrice);
    }

    [Fact]
    public async Task Of_twenty_equal_bids_at_once_one_wins()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();

        var statuses = await Concurrently.RunAsync(20, async i =>
            (await client.PostAsJsonAsync("/auctions/1/bids", new PlaceBid(i, 200))).StatusCode);

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
    }

    [Fact]
    public async Task The_local_bus_hands_a_bid_to_a_subscriber_on_the_same_instance()
    {
        await using var app = await App.StartAsync(infra);
        await using var subscription = await app.Bus.SubscribeAsync(1, CancellationToken.None);

        var bid = await app.CreateClient().PlaceBidAsync(1, 110);

        using var cts = new CancellationTokenSource(2000);
        Assert.Equal(bid, await subscription.Bids.ReadAsync(cts.Token));
    }
}
