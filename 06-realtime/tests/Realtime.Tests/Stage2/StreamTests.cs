namespace Realtime.Tests.Stage2;

// Stage 2 goal: one long response that carries every new bid as it happens.
[Collection("infra")]
public sealed class StreamTests(InfraFixture infra)
{
    [Fact]
    public async Task The_stream_starts_at_once_with_the_right_headers()
    {
        await using var app = await App.StartAsync(infra);

        await using var stream = await SseClient.ConnectAsync(app.CreateClient(), "/auctions/1/stream");

        Assert.Equal("text/event-stream", stream.Response.Content.Headers.ContentType?.MediaType);
        Assert.True(stream.Response.Headers.CacheControl?.NoCache, "Cache-Control: no-cache is missing");
    }

    [Fact]
    public async Task Each_new_bid_is_one_event_with_its_id()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        await using var stream = await SseClient.ConnectAsync(client, "/auctions/1/stream");

        var first = await client.PlaceBidAsync(1, 110, bidderId: 7);
        var second = await client.PlaceBidAsync(1, 120, bidderId: 8);

        var events = await stream.NextEventsAsync(2);
        Assert.Equal([first.Id.ToString(), second.Id.ToString()], events.Select(e => e.Id));
        Assert.All(events, e => Assert.Equal("bid", e.Event));
        Assert.Equal([first, second], events.Select(e => e.Bid));
        Assert.Equal(2, app.Stats.SentCount);
    }

    [Fact]
    public async Task A_fresh_stream_skips_old_bids_and_other_auctions()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        await client.PlaceBidAsync(1, 110);
        await using var stream = await SseClient.ConnectAsync(client, "/auctions/1/stream");

        await client.PlaceBidAsync(2, 110);
        var mine = await client.PlaceBidAsync(1, 120);

        Assert.Equal(mine, (await stream.NextEventAsync()).Bid);
        Assert.True(await stream.NoEventWithinAsync(300));
    }

    [Fact]
    public async Task An_idle_stream_sends_heartbeats()
    {
        await using var app = await App.StartAsync(infra);
        await using var stream = await SseClient.ConnectAsync(app.CreateClient(), "/auctions/1/stream");
        await Task.Delay(300);

        app.Time.Advance(TimeSpan.FromSeconds(App.HeartbeatSeconds));
        await stream.NextCommentAsync();
        app.Time.Advance(TimeSpan.FromSeconds(App.HeartbeatSeconds));
        await stream.NextCommentAsync();
    }

    [Fact]
    public async Task Leaving_releases_the_subscription()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        var stream = await SseClient.ConnectAsync(client, "/auctions/1/stream");
        await Eventually.AssertAsync(() => Assert.Equal(1, app.Stats.OpenSubscriptions));

        await stream.DisposeAsync();

        await Eventually.AssertAsync(() => Assert.Equal(0, app.Stats.OpenSubscriptions));
    }

    [Fact]
    public async Task Fifty_watchers_all_see_the_bid()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        var streams = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => SseClient.ConnectAsync(client, "/auctions/1/stream")));
        try
        {
            var bid = await client.PlaceBidAsync(1, 110);

            var seen = await Task.WhenAll(streams.Select(s => s.NextEventAsync()));
            Assert.All(seen, e => Assert.Equal(bid, e.Bid));
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }
}
