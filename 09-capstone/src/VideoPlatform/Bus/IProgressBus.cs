using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace VideoPlatform;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

// How a video's new state reaches the code holding watchers' streams.
public interface IProgressBus
{
    // Tell every subscriber to the video, on every instance the bus reaches.
    Task PublishAsync(VideoSnapshot snapshot, CancellationToken ct);

    // Start hearing the video's snapshots. When the task completes the subscription is live: a
    // snapshot published after that point arrives in Snapshots. Dispose to stop.
    Task<IProgressSubscription> SubscribeAsync(long videoId, CancellationToken ct);
}

public interface IProgressSubscription : IAsyncDisposable
{
    long VideoId { get; }
    ChannelReader<VideoSnapshot> Snapshots { get; }
}

// A subscription both buses use. It keeps the 16 newest snapshots: a watcher that falls behind
// loses old states, which newer ones replace anyway.
public sealed class ProgressSubscription : IProgressSubscription
{
    private readonly Channel<VideoSnapshot> _channel = Channel.CreateBounded<VideoSnapshot>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Func<ProgressSubscription, ValueTask> _onDispose;
    private int _disposed;

    public ProgressSubscription(long videoId, Func<ProgressSubscription, ValueTask> onDispose)
    {
        VideoId = videoId;
        _onDispose = onDispose;
    }

    public long VideoId { get; }
    public ChannelReader<VideoSnapshot> Snapshots => _channel.Reader;

    // Hand a snapshot to this subscriber. Never blocks.
    public void Deliver(VideoSnapshot snapshot) => _channel.Writer.TryWrite(snapshot);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _channel.Writer.TryComplete();
        await _onDispose(this);
    }
}

// Subscribers on this instance, by video. Both buses keep their local subscribers here.
public sealed class LocalSubscribers
{
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<ProgressSubscription, byte>> _subscribers = new();

    public void Deliver(VideoSnapshot snapshot)
    {
        if (_subscribers.TryGetValue(snapshot.VideoId, out var set))
        {
            foreach (var subscriber in set.Keys)
            {
                subscriber.Deliver(snapshot);
            }
        }
    }

    public ProgressSubscription Add(long videoId)
    {
        var subscription = new ProgressSubscription(videoId, s =>
        {
            if (_subscribers.TryGetValue(videoId, out var set))
            {
                set.TryRemove(s, out _);
            }
            return ValueTask.CompletedTask;
        });
        _subscribers.GetOrAdd(videoId, _ => new()).TryAdd(subscription, 0);
        return subscription;
    }

    public int Count => _subscribers.Values.Sum(s => s.Count);
}

// The baseline bus: in-process. Correct with one app instance.
public sealed class LocalProgressBus : IProgressBus
{
    private readonly LocalSubscribers _local = new();

    public Task PublishAsync(VideoSnapshot snapshot, CancellationToken ct)
    {
        _local.Deliver(snapshot);
        return Task.CompletedTask;
    }

    public Task<IProgressSubscription> SubscribeAsync(long videoId, CancellationToken ct) =>
        Task.FromResult<IProgressSubscription>(_local.Add(videoId));
}

// Reads a video's current state and publishes it. Everything that changes a video calls this
// after the change commits.
public sealed class Progress(VideoStore videos, IProgressBus bus, Stats stats)
{
    public async Task PublishAsync(long videoId, CancellationToken ct)
    {
        if (await videos.SnapshotAsync(videoId, ct) is { } snapshot)
        {
            stats.ProgressEvent();
            await bus.PublishAsync(snapshot, ct);
        }
    }
}
