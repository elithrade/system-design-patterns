using Microsoft.Extensions.Options;

namespace VideoPlatform;

// Stage 3, part 1: the progress bar, as Server-Sent Events (lab 6).
//
// GET /videos/{id}/progress. The goal, which the tests check:
//   - an unknown video is a 404 (set the status code and return before writing anything)
//   - otherwise: Content-Type text/event-stream, Cache-Control no-cache, X-Accel-Buffering no,
//     and the headers flushed at once
//   - the first event is the video's current state (VideoStore.SnapshotAsync), then each newer
//     state from the bus. Every event is
//         id: <version>
//         event: progress
//         data: <the VideoSnapshot as JSON, Json.Options>
//     followed by a blank line, and flushed
//   - never send a snapshot whose version is not above the last one sent: a bus may deliver an
//     older state after a newer one, and the snapshot you read may be newer than the next few
//     the bus hands you
//   - nothing may be lost between reading the state and hearing the bus: a change that commits
//     in between must still reach the watcher. A watcher that misses "published" waits forever
//   - after sending a final state (published, failed, abandoned) the stream ends
//   - with nothing to send for HeartbeatSeconds (on the injected TimeProvider), write the comment
//     line ": heartbeat" and a blank line
//   - when the client goes away (ct), stop and dispose the subscription
//
// Tests: dotnet test --filter-namespace "*Stage3"
public sealed class ProgressStream(VideoStore videos, IProgressBus bus, TimeProvider time, IOptions<LiveOptions> options)
{
    private readonly TimeSpan _heartbeat = TimeSpan.FromSeconds(options.Value.HeartbeatSeconds);

    public Task RunAsync(HttpResponse response, long videoId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
