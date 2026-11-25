using Microsoft.Extensions.Options;

namespace LongRunning;

// Runs one job: claim, render (with a heartbeat that extends the lease), complete or fail.
// The worker loops call it; the tests call it directly.
public sealed class JobRunner(IJobQueue queue, Renderer renderer, Stats stats, IOptions<JobsOptions> options, TimeProvider time,
    ILogger<JobRunner> logger)
{
    private readonly TimeSpan _heartbeat = TimeSpan.FromSeconds(options.Value.LeaseSeconds) / 3;

    // False if there was no job to run.
    public async Task<bool> RunOnceAsync(string workerId, CancellationToken ct)
    {
        var job = await queue.ClaimAsync(workerId, ct);
        if (job is null)
        {
            return false;
        }
        stats.Claim();

        using var running = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(job, running);
        try
        {
            var result = await renderer.RenderAsync(job.Kind, job.UserId, job.Year, running.Token);
            if (await queue.CompleteAsync(job, result, ct))
            {
                stats.Completion();
            }
            else
            {
                stats.RejectedCompletion();
                logger.LogWarning("Job {Id}: lost the lease before completing; result dropped", job.Id);
            }
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Job {Id}: lost the lease while rendering; stopped", job.Id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Job {Id} attempt {Attempt} failed: {Error}", job.Id, job.Attempt, e.Message);
            await queue.FailAsync(job, e.Message, ct);
        }
        finally
        {
            await running.CancelAsync();
            await heartbeat;
        }
        return true;
    }

    private async Task HeartbeatAsync(ClaimedJob job, CancellationTokenSource running)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_heartbeat, time, running.Token);
                if (!await queue.ExtendLeaseAsync(job, running.Token))
                {
                    await running.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

// Jobs:Workers loops, each running jobs one after another and polling when the queue is empty.
public sealed class Workers(JobRunner runner, IOptions<JobsOptions> options, ILogger<Workers> logger) : BackgroundService
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
