using Microsoft.Extensions.Options;

namespace VideoPlatform;

// Runs one rendition job: claim, transcode (resuming from the recorded percent), record progress
// as it goes, complete or fail, and publish the video's state after each change. The worker loops
// call it; the tests call it directly.
public sealed class TranscodeRunner(TranscodeQueue queue, Transcoder transcoder, Progress progress, Stats stats, ILogger<TranscodeRunner> logger)
{
    // False if there was nothing to run.
    public async Task<bool> RunOnceAsync(string workerId, CancellationToken ct)
    {
        var job = await queue.ClaimAsync(workerId, ct);
        if (job is null)
        {
            return false;
        }
        await progress.PublishAsync(job.VideoId, ct);

        using var running = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var size = await transcoder.RunAsync(job.VideoId, job.Name, job.Percent, async percent =>
            {
                if (!await queue.ProgressAsync(job, percent, ct))
                {
                    await running.CancelAsync();
                    running.Token.ThrowIfCancellationRequested();
                }
                await progress.PublishAsync(job.VideoId, ct);
            }, running.Token);

            switch (await queue.CompleteAsync(job, size, ct))
            {
                case CompleteResult.Published:
                    stats.Published();
                    break;
                case CompleteResult.Rejected:
                    logger.LogWarning("{Name} of video {Id}: lost the lease before completing", job.Name, job.VideoId);
                    break;
            }
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning("{Name} of video {Id}: lost the lease; stopped", job.Name, job.VideoId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("{Name} of video {Id} attempt {Attempt} failed: {Error}", job.Name, job.VideoId, job.Attempt, e.Message);
            await queue.FailAsync(job, e.Message, e is CorruptSourceException, ct);
        }
        await progress.PublishAsync(job.VideoId, ct);
        return true;
    }
}

// Transcode:Workers loops, each running jobs one after another and polling when the queue is empty.
public sealed class TranscodeWorkers(TranscodeRunner runner, IOptions<TranscodeOptions> options, ILogger<TranscodeWorkers> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, options.Value.Workers).Select(i => LoopAsync($"{Environment.MachineName}-{Environment.ProcessId}-{i}", stoppingToken)));

    private async Task LoopAsync(string workerId, CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await runner.RunOnceAsync(workerId, ct))
                {
                    await Task.Delay(options.Value.PollMilliseconds, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Worker {Worker} failed", workerId);
                await Task.Delay(1000, ct);
            }
        }
    }
}
