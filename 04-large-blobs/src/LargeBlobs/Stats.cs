using System.Diagnostics;

namespace LargeBlobs;

public sealed record StatsSnapshot(long BytesThroughApi, long PeakWorkingSetMb, long ManagedHeapMb);

// Counters the tests assert on and GET /stats returns.
public sealed class Stats
{
    private long _bytes;

    // file bytes the API itself read from a request or wrote to a response
    public long BytesThroughApi => Volatile.Read(ref _bytes);
    public void ThroughApi(long bytes) => Interlocked.Add(ref _bytes, bytes);

    public StatsSnapshot Snapshot()
    {
        using var process = Process.GetCurrentProcess();
        return new(BytesThroughApi, process.PeakWorkingSet64 / (1024 * 1024), GC.GetTotalMemory(false) / (1024 * 1024));
    }
}
