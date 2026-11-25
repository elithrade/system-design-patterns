using System.Diagnostics;

namespace VideoPlatform;

// Counters behind GET /stats.
public sealed class Stats
{
    private long _bytesThroughApi, _transcodes, _resumed, _transcodesDone, _published, _progressEvents, _openStreams;

    public void BytesThroughApi(long bytes) => Interlocked.Add(ref _bytesThroughApi, bytes);
    public void TranscodeStarted(int fromPercent)
    {
        Interlocked.Increment(ref _transcodes);
        if (fromPercent > 0)
        {
            Interlocked.Increment(ref _resumed);
        }
    }
    public void TranscodeDone() => Interlocked.Increment(ref _transcodesDone);
    public void Published() => Interlocked.Increment(ref _published);
    public void ProgressEvent() => Interlocked.Increment(ref _progressEvents);
    public void StreamOpened() => Interlocked.Increment(ref _openStreams);
    public void StreamClosed() => Interlocked.Decrement(ref _openStreams);

    public long BytesThroughApiCount => Interlocked.Read(ref _bytesThroughApi);
    public long Transcodes => Interlocked.Read(ref _transcodes);
    public long ResumedTranscodes => Interlocked.Read(ref _resumed);
    public long PublishedCount => Interlocked.Read(ref _published);
    public long OpenStreams => Interlocked.Read(ref _openStreams);

    public object Snapshot()
    {
        using var process = Process.GetCurrentProcess();
        return new
        {
            bytesThroughApi = BytesThroughApiCount,
            transcodesStarted = Transcodes,
            transcodesResumed = ResumedTranscodes,
            transcodesDone = Interlocked.Read(ref _transcodesDone),
            published = PublishedCount,
            progressEvents = Interlocked.Read(ref _progressEvents),
            openStreams = OpenStreams,
            peakWorkingSetMb = process.PeakWorkingSet64 / (1024 * 1024),
            managedHeapMb = GC.GetTotalMemory(false) / (1024 * 1024),
        };
    }
}
