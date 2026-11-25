using Microsoft.Extensions.Options;

namespace ScalingWrites;

// Runs BatchingViewCounter.FlushAsync every FlushMilliseconds on the injected clock, and once more
// when the app shuts down, so a clean stop loses nothing. A crash still loses the buffer.
public sealed class FlushLoop(
    BatchingViewCounter counter, IOptions<WritesOptions> options, TimeProvider time, ILogger<FlushLoop> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.FlushMilliseconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync(cancellationToken);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            await counter.FlushAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Flush failed; the views stay buffered for the next one");
        }
    }
}
