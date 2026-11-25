namespace Realtime;

public sealed record StatsSnapshot(
    string Instance,
    long Polls,
    long LongPolls,
    long Streams,
    long Sockets,
    long OpenConnections,
    long OpenSubscriptions,
    long Sent,
    long Dropped,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double LatencyMaxMs);

// Counters behind GET /stats (POST /stats/reset zeroes them). The endpoints count requests and
// connections; Subscription counts subscriptions and drops; Sent records delivery latency.
public sealed class Stats(Microsoft.Extensions.Options.IOptions<RealtimeOptions> options)
{
    private long _polls, _longPolls, _streams, _sockets, _open, _subscriptions, _sent, _dropped;
    private readonly List<double> _latencies = [];

    public long OpenSubscriptions => Volatile.Read(ref _subscriptions);
    public long SentCount => Volatile.Read(ref _sent);

    public void Poll() => Interlocked.Increment(ref _polls);
    public void LongPoll() => Interlocked.Increment(ref _longPolls);
    public void Stream() => Interlocked.Increment(ref _streams);
    public void Socket() => Interlocked.Increment(ref _sockets);
    public void Opened() => Interlocked.Increment(ref _open);
    public void Closed() => Interlocked.Decrement(ref _open);
    public void Subscribed() => Interlocked.Increment(ref _subscriptions);
    public void Unsubscribed() => Interlocked.Decrement(ref _subscriptions);
    public void Dropped() => Interlocked.Increment(ref _dropped);

    // Call once for every bid you write to a client: it records how long the bid took to get there.
    public void Sent(BidEvent bid)
    {
        Interlocked.Increment(ref _sent);
        var ms = (DateTime.UtcNow - bid.PlacedAt).TotalMilliseconds;
        lock (_latencies)
        {
            if (_latencies.Count < 1_000_000)
            {
                _latencies.Add(ms);
            }
        }
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _polls, 0);
        Interlocked.Exchange(ref _longPolls, 0);
        Interlocked.Exchange(ref _streams, 0);
        Interlocked.Exchange(ref _sockets, 0);
        Interlocked.Exchange(ref _sent, 0);
        Interlocked.Exchange(ref _dropped, 0);
        lock (_latencies)
        {
            _latencies.Clear();
        }
    }

    public StatsSnapshot Snapshot()
    {
        double[] sorted;
        lock (_latencies)
        {
            sorted = [.. _latencies];
        }
        Array.Sort(sorted);
        double At(double q) => sorted.Length == 0 ? 0 : Math.Round(sorted[(int)Math.Min(sorted.Length - 1, q * sorted.Length)], 1);
        return new(options.Value.Instance, Volatile.Read(ref _polls), Volatile.Read(ref _longPolls), Volatile.Read(ref _streams),
            Volatile.Read(ref _sockets), Volatile.Read(ref _open), OpenSubscriptions, SentCount, Volatile.Read(ref _dropped),
            At(0.5), At(0.95), sorted.Length == 0 ? 0 : Math.Round(sorted[^1], 1));
    }
}
