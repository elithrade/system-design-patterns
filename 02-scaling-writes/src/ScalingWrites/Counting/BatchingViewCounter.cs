namespace ScalingWrites;

// Stage 1. Count views in memory and write the sums in one statement every FlushMilliseconds.
// FlushLoop (already written) calls FlushAsync on that schedule, and once more when the app stops.
//
// The goal, which the tests check:
//   - RecordAsync never touches the database, and returns Accepted
//   - one FlushAsync writes everything buffered with ONE call to videos.AddViewsAsync, however many
//     views and videos; a flush with nothing buffered writes nothing
//   - a view recorded while a flush is running is not lost and not counted twice
//   - if AddViewsAsync throws, the views it was writing are kept for the next flush, and FlushAsync
//     rethrows
//
// RecordAsync is called by many requests at once, and FlushAsync can run at the same time.
//
// Tests: dotnet test --filter-namespace "*Stage1"
public sealed class BatchingViewCounter(VideoRepository videos) : IViewCounter
{
    public Task<RecordResult> RecordAsync(long videoId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task FlushAsync(CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    // Reads skip the buffer, so a view shows up after the next flush.
    public Task<long> GetViewsAsync(Video video, CancellationToken ct) => Task.FromResult(video.Views);
}
