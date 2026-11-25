using System.Collections.Concurrent;

namespace Contention;

public sealed record StatsSnapshot(IReadOnlyDictionary<string, long> Outcomes, long Conflicts);

// Counters the tests assert on and GET /stats returns.
public sealed class Stats
{
    private readonly ConcurrentDictionary<Outcome, long> _outcomes = new();
    private long _conflicts;

    // Stage 3: call Conflict() each time an optimistic write finds the version changed.
    public long Conflicts => Volatile.Read(ref _conflicts);
    public void Conflict() => Interlocked.Increment(ref _conflicts);

    // every answer the purchase endpoints gave, by outcome (the endpoints count these)
    public long Count(Outcome outcome) => _outcomes.GetValueOrDefault(outcome);
    public void Answered(Outcome outcome) => _outcomes.AddOrUpdate(outcome, 1, (_, n) => n + 1);

    public StatsSnapshot Snapshot() => new(
        _outcomes.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        Conflicts);
}
