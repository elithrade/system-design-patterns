namespace VideoPlatform;

// What happens to a video after POST /videos and after its upload is confirmed. Pipeline:Engine
// picks one. Both calls must be safe to repeat.
public interface IPipeline
{
    Task OnCreatedAsync(long videoId, CancellationToken ct);

    // Inline returns when the video is published or failed; the others return once the work is
    // handed off.
    Task OnUploadedAsync(long videoId, CancellationToken ct);
}

// The baseline: make every rendition, one after another, inside the request that confirmed the
// upload. Progress is recorded and published as it goes, so a stream (stage 3) works with it too.
public sealed class InlinePipeline(VideoStore videos, Transcoder transcoder, Progress progress, Stats stats,
    Microsoft.Extensions.Options.IOptions<TranscodeOptions> options) : IPipeline
{
    public Task OnCreatedAsync(long videoId, CancellationToken ct) => Task.CompletedTask;

    public async Task OnUploadedAsync(long videoId, CancellationToken ct)
    {
        // someone else is already on it, or it is finished
        if (!await videos.SetStatusAsync(videoId, [VideoStatus.Uploaded], VideoStatus.Processing, null, ct))
        {
            return;
        }
        var names = options.Value.Renditions.Keys.Order().ToList();
        await videos.CreateRenditionsAsync(videoId, names, ct);
        await progress.PublishAsync(videoId, ct);
        foreach (var name in names)
        {
            try
            {
                await videos.SetRenditionAsync(videoId, name, "running", 0, null, null, ct);
                var size = await transcoder.RunAsync(videoId, name, 0, async percent =>
                {
                    await videos.SetRenditionAsync(videoId, name, "running", percent, null, null, ct);
                    await progress.PublishAsync(videoId, ct);
                }, ct);
                await videos.SetRenditionAsync(videoId, name, "done", 100, size, null, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await videos.SetRenditionAsync(videoId, name, "failed", 0, null, e.Message, ct);
                await videos.SetStatusAsync(videoId, [VideoStatus.Processing], VideoStatus.Failed, e.Message, ct);
                await progress.PublishAsync(videoId, ct);
                return;
            }
        }
        if (await videos.SetStatusAsync(videoId, [VideoStatus.Processing], VideoStatus.Published, null, ct))
        {
            stats.Published();
        }
        await progress.PublishAsync(videoId, ct);
    }
}

// Stage 2: queue the renditions; TranscodeWorkers make them.
public sealed class QueuePipeline(TranscodeQueue queue, Progress progress) : IPipeline
{
    public Task OnCreatedAsync(long videoId, CancellationToken ct) => Task.CompletedTask;

    public async Task OnUploadedAsync(long videoId, CancellationToken ct)
    {
        await queue.EnqueueAsync(videoId, ct);
        await progress.PublishAsync(videoId, ct);
    }
}
