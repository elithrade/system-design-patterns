using System.Collections.Concurrent;
using System.Text;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace VideoPlatform;

// Worth retrying: the transcoder crashed, a node went away.
public sealed class TranscodeFailedException(string message) : Exception(message);

// Not worth retrying: the source is not a video it can read. A source whose bytes start with
// "CORRUPT" is one (k6 uploads some on purpose).
public sealed class CorruptSourceException(string message) : Exception(message);

// Failures the tests plan.
public sealed class Faults
{
    private readonly ConcurrentDictionary<string, int> _failures = new();
    private readonly ConcurrentDictionary<string, int> _hangs = new();

    // The next `times` transcodes of this rendition fail at 50% with TranscodeFailedException.
    public void FailNext(string rendition, int times) => _failures[rendition] = times;

    // The next transcode of this rendition stops at `percent` and waits until it is cancelled, as
    // a process that froze or died would.
    public void HangAt(string rendition, int percent) => _hangs[rendition] = percent;

    internal bool TakeFailure(string rendition)
    {
        while (_failures.TryGetValue(rendition, out var left) && left > 0)
        {
            if (_failures.TryUpdate(rendition, left - 1, left))
            {
                return true;
            }
        }
        return false;
    }

    internal bool TakeHang(string rendition, int percent) =>
        _hangs.TryGetValue(rendition, out var at) && at == percent && _hangs.TryRemove(new(rendition, at));
}

// A stand-in for ffmpeg: it spends MsPerMb x the rendition's cost per MB of source, reports progress
// every 10%, and writes a small rendition object. It is not a real transcoder; the time it takes
// and how it fails are what matter here.
public sealed class Transcoder(Blobs blobs, Faults faults, Stats stats, IOptions<TranscodeOptions> options)
{
    private const string CorruptMarker = "CORRUPT";

    // Makes one rendition, starting at fromPercent (a retry that resumes skips the work before
    // it), and calls progress(10), progress(20) ... progress(100) as it goes. Returns the
    // rendition's size.
    public async Task<long> RunAsync(long videoId, string rendition, int fromPercent, Func<int, Task> progress, CancellationToken ct)
    {
        if (!options.Value.Renditions.TryGetValue(rendition, out var cost))
        {
            throw new CorruptSourceException($"no rendition called {rendition}");
        }
        stats.TranscodeStarted(fromPercent);

        var size = await blobs.SizeAsync(Blobs.SourceKey(videoId), ct)
            ?? throw new CorruptSourceException($"video {videoId} has no source");
        using (var head = await blobs.S3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = blobs.Bucket,
            Key = Blobs.SourceKey(videoId),
            ByteRange = new ByteRange(0, CorruptMarker.Length - 1),
        }, ct))
        {
            var buffer = new byte[CorruptMarker.Length];
            var read = await head.ResponseStream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
            if (Encoding.ASCII.GetString(buffer, 0, read) == CorruptMarker)
            {
                throw new CorruptSourceException($"video {videoId}: not a video");
            }
        }

        var step = TimeSpan.FromMilliseconds(size / (1024.0 * 1024.0) * options.Value.MsPerMb * cost / 10);
        for (var percent = fromPercent / 10 * 10 + 10; percent <= 100; percent += 10)
        {
            await Task.Delay(step, ct);
            if (percent == 50 && faults.TakeFailure(rendition))
            {
                throw new TranscodeFailedException($"{rendition} of video {videoId}: the transcoder crashed at 50%");
            }
            await progress(percent);
            if (faults.TakeHang(rendition, percent))
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        }

        var output = Encoding.UTF8.GetBytes($"{rendition} rendition of video {videoId} ({size} bytes of source)\n");
        await blobs.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = blobs.Bucket,
            Key = Blobs.RenditionKey(videoId, rendition),
            InputStream = new MemoryStream(output),
            ContentType = "text/plain",
        }, ct);
        stats.TranscodeDone();
        return output.Length;
    }
}
