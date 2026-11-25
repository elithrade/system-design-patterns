namespace ScalingWrites;

// Stage 4's counter: accept into the queue (202) or shed (503). Reads see videos.views, so a view
// shows up once the drainer has written it.
public sealed class QueuedViewCounter(ViewQueue queue, Stats stats) : IViewCounter
{
    public Task<RecordResult> RecordAsync(long videoId, CancellationToken ct)
    {
        if (queue.TryEnqueue(videoId))
        {
            stats.Accept();
            return Task.FromResult(RecordResult.Accepted);
        }
        stats.Shedding();
        return Task.FromResult(RecordResult.Shed);
    }

    public Task<long> GetViewsAsync(Video video, CancellationToken ct) => Task.FromResult(video.Views);
}

// Drains the queue for as long as the app runs. A failed write is logged and its views are lost:
// this queue lives in memory, so a crash loses everything in it anyway.
public sealed class QueueDrainer(ViewQueue queue, VideoRepository videos, ILogger<QueueDrainer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await queue.DrainOnceAsync(videos, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Drain failed");
                await Task.Delay(100, stoppingToken);
            }
        }
    }
}
