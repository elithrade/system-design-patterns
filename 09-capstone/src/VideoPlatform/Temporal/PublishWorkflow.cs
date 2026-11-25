using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace VideoPlatform;

// What TemporalPipeline starts the workflow with, at POST /videos.
public sealed record PublishInput(
    long VideoId,
    IReadOnlyList<string> Renditions,
    int UploadWaitSeconds,
    int MaxAttempts,
    int BackoffSeconds,
    int HeartbeatTimeoutSeconds);

// Stage 4: one workflow per video, from POST /videos to published.
//
// RunAsync returns the video's final status ("published", "failed" or "abandoned"):
//   - wait for the Uploaded signal, at most UploadWaitSeconds (Workflow.WaitConditionAsync takes a
//     timeout; the timer is durable: it fires even if every worker restarts meanwhile). If the
//     upload never comes, AbandonAsync and return
//   - StartProcessingAsync, then transcode every rendition at the same time (TranscodeAsync, one
//     activity each). Activity options:
//       - RetryPolicy: InitialInterval = BackoffSeconds, BackoffCoefficient 2,
//         MaximumAttempts = MaxAttempts; a corrupt source (CorruptSourceException) is not worth
//         retrying
//       - HeartbeatTimeout = HeartbeatTimeoutSeconds: TranscodeAsync heartbeats its percent, so
//         an attempt that stops heartbeating (a dead or frozen worker) is retried elsewhere, and
//         the retry resumes from the last percent it heard
//       - a StartToCloseTimeout long enough for the biggest video (an hour)
//   - every rendition done: PublishAsync. A rendition that fails for good: FailAsync with the
//     failure's message (an ActivityFailureException's InnerException is an
//     ApplicationFailureException), which deletes the renditions already made
//   - Uploaded can arrive twice (a retried confirm): no harm
// Workflow code must be deterministic: no DateTime.Now, Random, I/O or Task.Delay. Workflow.*
// has replacements (Workflow.WhenAllAsync, Workflow.DelayAsync, ...).
//
// Run it with Pipeline__Engine=Temporal and the compose stack's Temporal server.
// Tests: dotnet test --filter-namespace "*Stage4"
[Workflow]
public class PublishWorkflow
{
    [WorkflowRun]
    public Task<string> RunAsync(PublishInput input)
    {
        throw new NotImplementedException();
    }

    [WorkflowSignal]
    public Task UploadedAsync()
    {
        throw new NotImplementedException();
    }
}
