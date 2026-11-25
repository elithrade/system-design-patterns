namespace Realtime.Tests.Stage5;

// Stage 5 goal: a client that drops and reconnects with Last-Event-ID misses nothing and sees
// nothing twice.
[Collection("infra")]
public sealed class ResumeTests(InfraFixture infra)
{
    [Fact]
    public async Task Reconnecting_replays_what_was_missed_then_goes_live()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        var placed = new List<BidEvent>();
        for (var i = 1; i <= 5; i++)
        {
            placed.Add(await client.PlaceBidAsync(1, 100 + i * 10));
        }

        await using var stream = await SseClient.ConnectAsync(client, "/auctions/1/stream", lastEventId: placed[2].Id);
        var replayed = await stream.NextEventsAsync(2);
        var live = await client.PlaceBidAsync(1, 500);

        Assert.Equal([placed[3].Id, placed[4].Id], replayed.Select(e => e.Bid.Id));
        Assert.Equal(live, (await stream.NextEventAsync()).Bid);
        Assert.True(await stream.NoEventWithinAsync(300));
    }

    [Fact]
    public async Task More_missed_bids_than_one_page_all_arrive_in_order()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        var first = await client.PlaceBidAsync(1, 101);
        for (var i = 2; i <= 250; i++)
        {
            await app.BidAsync(1, 100 + i);
        }

        await using var stream = await SseClient.ConnectAsync(client, "/auctions/1/stream", lastEventId: first.Id);
        var events = await stream.NextEventsAsync(249);

        Assert.Equal(Enumerable.Range(0, 249).Select(i => first.Id + 1 + i), events.Select(e => e.Bid.Id));
        Assert.True(await stream.NoEventWithinAsync(300));
    }

    [Fact]
    public async Task A_bid_right_after_the_replay_read_is_not_missed()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        var seen = await client.PlaceBidAsync(1, 110);
        var missed = await client.PlaceBidAsync(1, 120);
        BidEvent? sneaky = null;
        app.Hooks.AfterNextRead(async () => sneaky = await app.BidAsync(1, 130));

        await using var stream = await SseClient.ConnectAsync(client, "/auctions/1/stream", lastEventId: seen.Id);
        var events = await stream.NextEventsAsync(2);

        Assert.Equal([missed.Id, sneaky!.Id], events.Select(e => e.Bid.Id));
        Assert.True(await stream.NoEventWithinAsync(300));
    }

    [Fact]
    public async Task A_bid_right_before_the_replay_read_arrives_once()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        var seen = await client.PlaceBidAsync(1, 110);
        BidEvent? sneaky = null;
        app.Hooks.BeforeNextRead(async () => sneaky = await app.BidAsync(1, 120));

        await using var stream = await SseClient.ConnectAsync(client, "/auctions/1/stream", lastEventId: seen.Id);

        var first = await stream.NextEventAsync();
        Assert.Equal(sneaky!.Id, first.Bid.Id);
        Assert.True(await stream.NoEventWithinAsync(500), "the bid arrived twice");
    }

    [Fact]
    public async Task Nothing_missed_means_nothing_replayed()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        var last = await client.PlaceBidAsync(1, 110);

        await using var stream = await SseClient.ConnectAsync(client, "/auctions/1/stream", lastEventId: last.Id);

        Assert.True(await stream.NoEventWithinAsync(300));
        var next = await client.PlaceBidAsync(1, 120);
        Assert.Equal(next, (await stream.NextEventAsync()).Bid);
    }
}
