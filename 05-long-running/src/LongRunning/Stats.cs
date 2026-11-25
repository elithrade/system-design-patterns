namespace LongRunning;

public sealed record StatsSnapshot(long Renders, long RenderFailures, long Claims, long Completions, long RejectedCompletions);

// Counters the tests assert on and GET /stats returns. The runner and renderer update them.
public sealed class Stats
{
    private long _renders, _renderFailures, _claims, _completions, _rejected;

    // renders started (a job rendered twice counts twice)
    public long Renders => Volatile.Read(ref _renders);
    public long RenderFailures => Volatile.Read(ref _renderFailures);
    public long Claims => Volatile.Read(ref _claims);
    public long Completions => Volatile.Read(ref _completions);
    // completions the queue refused (the worker had lost its lease)
    public long RejectedCompletions => Volatile.Read(ref _rejected);

    public void Render() => Interlocked.Increment(ref _renders);
    public void RenderFailure() => Interlocked.Increment(ref _renderFailures);
    public void Claim() => Interlocked.Increment(ref _claims);
    public void Completion() => Interlocked.Increment(ref _completions);
    public void RejectedCompletion() => Interlocked.Increment(ref _rejected);

    public StatsSnapshot Snapshot() => new(Renders, RenderFailures, Claims, Completions, RejectedCompletions);
}

// Failures the tests switch on.
public sealed class Faults
{
    private int _failingRenders;

    // The next n renders throw.
    public void FailNextRenders(int n) => Volatile.Write(ref _failingRenders, n);

    internal bool ShouldFail()
    {
        while (true)
        {
            var left = Volatile.Read(ref _failingRenders);
            if (left <= 0)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref _failingRenders, left - 1, left) == left)
            {
                return true;
            }
        }
    }
}
