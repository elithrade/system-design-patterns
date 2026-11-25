using System.Net.WebSockets;
using System.Text.Json;

namespace Realtime.Tests.Stage3;

// Stage 3 goal: one connection both ways: bids come in on it and news goes out on it.
// These run the app on Kestrel with real sockets.
[Collection("infra")]
public sealed class SocketTests(InfraFixture infra)
{
    private static BidEvent BidOf(JsonElement message) => message.GetProperty("bid").Deserialize<BidEvent>(Json.Options)!;

    [Fact]
    public async Task A_bid_placed_over_http_reaches_the_socket()
    {
        await using var app = await App.StartAsync(infra, kestrel: true);
        await using var socket = await SocketClient.ConnectAsync(app, 1);
        await Eventually.AssertAsync(() => Assert.Equal(1, app.Stats.OpenSubscriptions));

        var bid = await app.CreateClient().PlaceBidAsync(1, 110);

        var message = await socket.NextAsync();
        Assert.Equal("bid", message.GetProperty("type").GetString());
        Assert.Equal(bid, BidOf(message));
        Assert.Equal(1, app.Stats.SentCount);
    }

    [Fact]
    public async Task A_bid_over_the_socket_gets_a_result_and_reaches_other_watchers()
    {
        await using var app = await App.StartAsync(infra, kestrel: true);
        await using var bidder = await SocketClient.ConnectAsync(app, 1);
        await using var watcher = await SocketClient.ConnectAsync(app, 1);
        await Eventually.AssertAsync(() => Assert.Equal(2, app.Stats.OpenSubscriptions));

        await bidder.BidAsync("r1", 150, bidderId: 7);

        var result = await bidder.NextOfTypeAsync("result");
        Assert.Equal(("r1", "Accepted", 150L),
            (result.GetProperty("ref").GetString(), result.GetProperty("outcome").GetString(), result.GetProperty("currentPrice").GetInt64()));
        var seen = BidOf(await watcher.NextOfTypeAsync("bid"));
        Assert.Equal((7L, 150L), (seen.BidderId, seen.Amount));
    }

    [Fact]
    public async Task A_losing_bid_gets_a_result_that_says_why()
    {
        await using var app = await App.StartAsync(infra, kestrel: true);
        await app.CreateClient().PlaceBidAsync(1, 300);
        await using var socket = await SocketClient.ConnectAsync(app, 1);

        await socket.BidAsync("low", 200);

        var result = await socket.NextOfTypeAsync("result");
        Assert.Equal(("low", "TooLow", 300L),
            (result.GetProperty("ref").GetString(), result.GetProperty("outcome").GetString(), result.GetProperty("currentPrice").GetInt64()));
    }

    [Fact]
    public async Task Garbage_gets_an_error_and_the_socket_stays_open()
    {
        await using var app = await App.StartAsync(infra, kestrel: true);
        await using var socket = await SocketClient.ConnectAsync(app, 1);

        await socket.SendTextAsync("this is not json");
        Assert.Equal("error", (await socket.NextAsync()).GetProperty("type").GetString());

        await socket.BidAsync("ok", 110);
        Assert.Equal("Accepted", (await socket.NextOfTypeAsync("result")).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Closing_completes_the_handshake_and_releases_the_subscription()
    {
        await using var app = await App.StartAsync(infra, kestrel: true);
        var socket = await SocketClient.ConnectAsync(app, 1);
        await Eventually.AssertAsync(() => Assert.Equal(1, app.Stats.OpenSubscriptions));

        await socket.CloseAsync();

        Assert.Equal(WebSocketState.Closed, socket.Socket.State);
        await Eventually.AssertAsync(() => Assert.Equal(0, app.Stats.OpenSubscriptions));
        await socket.DisposeAsync();
    }

    [Fact]
    public async Task Bids_from_both_directions_at_once_arrive_whole()
    {
        await using var app = await App.StartAsync(infra, kestrel: true);
        var client = app.CreateClient();
        await using var socket = await SocketClient.ConnectAsync(app, 1);
        await Eventually.AssertAsync(() => Assert.Equal(1, app.Stats.OpenSubscriptions));

        // 40 bids over HTTP and 40 over the socket, all at once, all different amounts
        await Concurrently.RunAsync(80, async i =>
        {
            if (i % 2 == 0)
            {
                await client.PostAsync("/auctions/1/bids", System.Net.Http.Json.JsonContent.Create(new PlaceBid(i, 1000 + i)));
            }
            else
            {
                await socket.BidAsync($"s{i}", 1000 + i);
            }
        });

        var results = 0;
        var bids = new List<BidEvent>();
        while (results < 40)
        {
            var message = await socket.NextAsync(5000);
            if (message.GetProperty("type").GetString() == "result")
            {
                results++;
            }
            else
            {
                bids.Add(BidOf(message));
            }
        }
        var accepted = (await client.GetAsync("/auctions/1/bids?after=0")).Content.ReadFromJsonAsync<List<BidEvent>>(Json.Options);
        var expected = (await accepted)!.Select(b => b.Id).ToList();
        while (bids.Count < expected.Count)
        {
            bids.Add(BidOf(await socket.NextOfTypeAsync("bid")));
        }
        Assert.Empty(socket.Invalid);
        Assert.Equal(expected.Order(), bids.Select(b => b.Id).Order());
    }
}

file static class ContentExtensions
{
    public static Task<T?> ReadFromJsonAsync<T>(this HttpContent content, JsonSerializerOptions options) =>
        System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<T>(content, options);
}
