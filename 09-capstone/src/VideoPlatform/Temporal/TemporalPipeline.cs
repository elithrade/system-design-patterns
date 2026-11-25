using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Worker;

namespace VideoPlatform;

// Engine=Temporal: POST /videos starts the video's workflow; a confirmed upload signals it.
public sealed class TemporalPipeline(ITemporalClient client, IOptions<TemporalOptions> temporal, IOptions<TranscodeOptions> transcode) : IPipeline
{
    public string WorkflowId(long videoId) => $"{temporal.Value.WorkflowIdPrefix}video-{videoId}";

    public async Task OnCreatedAsync(long videoId, CancellationToken ct)
    {
        var input = new PublishInput(videoId, transcode.Value.Renditions.Keys.Order().ToList(), temporal.Value.UploadWaitSeconds,
            transcode.Value.MaxAttempts, transcode.Value.BackoffSeconds, temporal.Value.HeartbeatTimeoutSeconds);
        await client.StartWorkflowAsync((PublishWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(WorkflowId(videoId), temporal.Value.TaskQueue) { Rpc = new() { CancellationToken = ct } });
    }

    public Task OnUploadedAsync(long videoId, CancellationToken ct) =>
        client.GetWorkflowHandle<PublishWorkflow>(WorkflowId(videoId))
            .SignalAsync(wf => wf.UploadedAsync(), new() { Rpc = new() { CancellationToken = ct } });
}

// A Temporal worker in this process (Engine=Temporal): it runs workflows and activities.
public sealed class TemporalWorkerService(ITemporalClient client, PublishActivities activities, IOptions<TemporalOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var worker = new TemporalWorker(client, new TemporalWorkerOptions(options.Value.TaskQueue)
            {
                // the stub's NotImplementedException fails the workflow instead of retrying it forever
                WorkflowFailureExceptionTypes = [typeof(NotImplementedException)],
            }
            .AddAllActivities(activities)
            .AddWorkflow<PublishWorkflow>());
        await worker.ExecuteAsync(stoppingToken);
    }
}
