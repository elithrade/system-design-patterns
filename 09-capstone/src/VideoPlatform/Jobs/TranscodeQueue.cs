using Microsoft.Extensions.Options;

namespace VideoPlatform;

// A rendition a worker has claimed. Percent is the progress recorded before this attempt.
public sealed record ClaimedRendition(long VideoId, string Name, int Attempt, string WorkerId, DateTime LeaseUntil, int Percent);

public enum CompleteResult
{
    // this worker no longer holds the rendition (its lease lapsed and someone else claimed it)
    Rejected,
    // the rendition is done; others are not yet
    Done,
    // the rendition is done and it was the video's last: this call published the video
    Published,
}

// Stage 2: renditions as jobs. Each row of the renditions table is a job (lab 5's queue).
//
//   - EnqueueAsync: move the video from uploaded to processing and queue one rendition per name in
//     Transcode:Renditions. Called again (a retried confirm), it changes nothing
//   - ClaimAsync: one statement or one transaction. Take a queued rendition whose run_after has
//     come, or a running one whose lease has lapsed, of a video that is processing; skip rows
//     another worker holds (FOR UPDATE SKIP LOCKED). Mark it running, count the attempt, set
//     locked_by and lease_until (now + LeaseSeconds). Return null when there is nothing to do
//   - ProgressAsync: record the percent and extend the lease, only if this worker still holds this
//     attempt. False if it does not: the runner then stops
//   - CompleteAsync: mark the rendition done (size, percent 100), only if this worker still holds
//     this attempt. If that made every rendition of the video done, move the video from
//     processing to published. Two workers finishing a video's last two renditions at the same
//     moment: exactly one of them gets Published
//   - FailAsync: if this worker still holds this attempt: queue it again after
//     BackoffSeconds * 2^(attempt - 1), or, when the failure is permanent or this was attempt
//     MaxAttempts, mark the rendition failed and the video failed (reason: the error). True if
//     this call failed the video
// Times come from the injected TimeProvider. The runner (TranscodeRunner) calls all of these.
//
// Tests: dotnet test --filter-namespace "*Stage2"
public sealed class TranscodeQueue(VideoStore videos, TimeProvider time, IOptions<TranscodeOptions> options)
{
    public Task EnqueueAsync(long videoId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<ClaimedRendition?> ClaimAsync(string workerId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<bool> ProgressAsync(ClaimedRendition job, int percent, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<CompleteResult> CompleteAsync(ClaimedRendition job, long size, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<bool> FailAsync(ClaimedRendition job, string error, bool permanent, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
