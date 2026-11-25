namespace Realtime.Tests.Stage4;

// Stage 4 goal: two app instances, one auction. A watcher on one instance hears a bid placed on
// the other. These use the bus directly, so they do not depend on stages 1-3.
[Collection("infra")]
public sealed class RedisBusTests(InfraFixture infra)
{
    private static async Task<BidEvent> NextAsync(IBidSubscription subscription, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        return await subscription.Bids.ReadAsync(cts.Token);
    }

    private static async Task<bool> NothingWithinAsync(IBidSubscription subscription, int ms)
    {
        await Task.Delay(ms);
        return !subscription.Bids.TryPeek(out _);
    }

    [Fact]
    public async Task A_bid_on_one_instance_reaches_a_watcher_on_the_other()
    {
        await using var a = await App.StartAsync(infra, BusKind.Redis);
        await using var b = await App.StartAsync(infra, BusKind.Redis, sameBackendAs: a);
        await using var watcher = await b.Bus.SubscribeAsync(1, CancellationToken.None);

        var bid = await a.CreateClient().PlaceBidAsync(1, 110);

        Assert.Equal(bid, await NextAsync(watcher));
    }

    [Fact]
    public async Task A_watcher_on_the_same_instance_gets_each_bid_once()
    {
        await using var a = await App.StartAsync(infra, BusKind.Redis);
        await using var watcher = await a.Bus.SubscribeAsync(1, CancellationToken.None);

        var bid = await a.CreateClient().PlaceBidAsync(1, 110);

        Assert.Equal(bid, await NextAsync(watcher));
        Assert.True(await NothingWithinAsync(watcher, 500), "the bid arrived twice");
    }

    [Fact]
    public async Task Each_watcher_hears_only_its_auction()
    {
        await using var a = await App.StartAsync(infra, BusKind.Redis);
        await using var b = await App.StartAsync(infra, BusKind.Redis, sameBackendAs: a);
        await using var one = await b.Bus.SubscribeAsync(1, CancellationToken.None);
        await using var two = await b.Bus.SubscribeAsync(2, CancellationToken.None);
        var client = a.CreateClient();

        var bid1 = await client.PlaceBidAsync(1, 110);
        var bid2 = await client.PlaceBidAsync(2, 120);

        Assert.Equal(bid1, await NextAsync(one));
        Assert.Equal(bid2, await NextAsync(two));
        Assert.True(await NothingWithinAsync(one, 300));
    }

    [Fact]
    public async Task Leaving_stops_only_that_watcher()
    {
        await using var a = await App.StartAsync(infra, BusKind.Redis);
        await using var b = await App.StartAsync(infra, BusKind.Redis, sameBackendAs: a);
        var leaving = await b.Bus.SubscribeAsync(1, CancellationToken.None);
        await using var staying = await b.Bus.SubscribeAsync(1, CancellationToken.None);

        await leaving.DisposeAsync();
        var bid = await a.CreateClient().PlaceBidAsync(1, 110);

        Assert.Equal(bid, await NextAsync(staying));
        Assert.False(leaving.Bids.TryRead(out _), "a disposed subscription still received a bid");
    }

    [Fact]
    public async Task When_nobody_on_an_instance_watches_it_unsubscribes_in_redis()
    {
        await using var b = await App.StartAsync(infra, BusKind.Redis);
        var channel = $"{b.Prefix}auction:1";
        var first = await b.Bus.SubscribeAsync(1, CancellationToken.None);
        var second = await b.Bus.SubscribeAsync(1, CancellationToken.None);
        Assert.Equal(1, await infra.SubscribersAsync(channel));

        await first.DisposeAsync();
        Assert.Equal(1, await infra.SubscribersAsync(channel));
        await second.DisposeAsync();

        await Eventually.AssertAsync(async () => Assert.Equal(0, await infra.SubscribersAsync(channel)));
        Assert.Equal(0, b.Stats.OpenSubscriptions);
    }
}
