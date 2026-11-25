namespace ScalingWrites;

public enum RecordResult
{
    // written before the response: 204
    Written,
    // taken, and written later: 202
    Accepted,
    // no such video: 404
    NotFound,
    // turned away, try later: 503 with Retry-After
    Shed,
}

// Records views and reads them back. One implementation per stage; Writes:Counter picks it.
public interface IViewCounter
{
    Task<RecordResult> RecordAsync(long videoId, CancellationToken ct);

    // The count to show for a video the main database has. video.Views is its videos.views column.
    Task<long> GetViewsAsync(Video video, CancellationToken ct);
}

// The baseline: every view is an UPDATE on the video's row.
public sealed class DirectViewCounter(VideoRepository videos) : IViewCounter
{
    public async Task<RecordResult> RecordAsync(long videoId, CancellationToken ct) =>
        await videos.AddViewAsync(videoId, ct) ? RecordResult.Written : RecordResult.NotFound;

    public Task<long> GetViewsAsync(Video video, CancellationToken ct) => Task.FromResult(video.Views);
}
