using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Realtime;

// How a placed bid reaches the code holding watchers' connections.
public interface IBidBus
{
    // Tell every subscriber to the bid's auction, on every instance the bus reaches.
    Task PublishAsync(BidEvent bid, CancellationToken ct);

    // Start hearing the auction's bids. When the task completes, the subscription is live: a bid
    // published after that point arrives in Bids. Dispose to stop.
    Task<IBidSubscription> SubscribeAsync(long auctionId, CancellationToken ct);
}

public interface IBidSubscription : IAsyncDisposable
{
    long AuctionId { get; }
    ChannelReader<BidEvent> Bids { get; }
}

// A subscription both buses use: a bounded in-memory queue per subscriber. If a subscriber falls
// 1,024 bids behind, new bids are dropped for it (Stats.Dropped counts them).
public sealed class Subscription : IBidSubscription
{
    private readonly Channel<BidEvent> _channel;
    private readonly Func<Subscription, ValueTask> _onDispose;
    private readonly Stats _stats;
    private int _disposed;

    public Subscription(long auctionId, Stats stats, Func<Subscription, ValueTask> onDispose)
    {
        AuctionId = auctionId;
        _stats = stats;
        _onDispose = onDispose;
        _channel = Channel.CreateBounded<BidEvent>(
            new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
            _ => stats.Dropped());
        stats.Subscribed();
    }

    public long AuctionId { get; }
    public ChannelReader<BidEvent> Bids => _channel.Reader;

    // Hand a bid to this subscriber. Never blocks.
    public void Deliver(BidEvent bid) => _channel.Writer.TryWrite(bid);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _channel.Writer.TryComplete();
        _stats.Unsubscribed();
        await _onDispose(this);
    }
}

// The baseline bus: in-process. Correct with one app instance.
public sealed class LocalBidBus(Stats stats) : IBidBus
{
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<Subscription, byte>> _subscribers = new();

    public Task PublishAsync(BidEvent bid, CancellationToken ct)
    {
        if (_subscribers.TryGetValue(bid.AuctionId, out var subscribers))
        {
            foreach (var subscriber in subscribers.Keys)
            {
                subscriber.Deliver(bid);
            }
        }
        return Task.CompletedTask;
    }

    public Task<IBidSubscription> SubscribeAsync(long auctionId, CancellationToken ct)
    {
        var subscription = new Subscription(auctionId, stats, s =>
        {
            if (_subscribers.TryGetValue(auctionId, out var set))
            {
                set.TryRemove(s, out _);
            }
            return ValueTask.CompletedTask;
        });
        _subscribers.GetOrAdd(auctionId, _ => new()).TryAdd(subscription, 0);
        return Task.FromResult<IBidSubscription>(subscription);
    }
}
