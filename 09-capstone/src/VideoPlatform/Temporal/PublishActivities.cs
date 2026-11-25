using Temporalio.Activities;

namespace VideoPlatform;

// Temporal activities: everything the workflow does that touches the world. Each records its
// change in Postgres and publishes the video's new state, so the progress stream (stage 3) works
// with this engine too. An exception's type name becomes the failure's ErrorType (for example
// "CorruptSourceException").
public sealed class PublishActivities(VideoStore videos, Blobs blobs, Transcoder transcoder, Progress progress, Stats stats)
{
    private static CancellationToken Ct => ActivityExecutionContext.Current.CancellationToken;

    // The upload never came: mark the video abandoned and delete a source that arrived too late.
    [Activity]
    public async Task AbandonAsync(long videoId)
    {
        await videos.SetStatusAsync(videoId, [VideoStatus.Pending, VideoStatus.Uploaded], VideoStatus.Abandoned, "the upload never arrived", Ct);
        await blobs.DeleteAsync([Blobs.SourceKey(videoId)], Ct);
        await progress.PublishAsync(videoId, Ct);
    }

    [Activity]
    public async Task StartProcessingAsync(long videoId, IReadOnlyList<string> renditions)
    {
        await videos.SetStatusAsync(videoId, [VideoStatus.Uploaded], VideoStatus.Processing, null, Ct);
        await videos.CreateRenditionsAsync(videoId, renditions, Ct);
        await progress.PublishAsync(videoId, Ct);
    }

    // Makes one rendition, heartbeating its percent. A retry starts from the percent of the last
    // heartbeat the server recorded.
    [Activity]
    public async Task<long> TranscodeAsync(long videoId, string rendition)
    {
        var context = ActivityExecutionContext.Current;
        var from = context.Info.HeartbeatDetails.Count > 0 ? await context.Info.HeartbeatDetailAtAsync<int>(0) : 0;
        await videos.SetRenditionAsync(videoId, rendition, "running", from, null, null, Ct);
        await progress.PublishAsync(videoId, Ct);
        var size = await transcoder.RunAsync(videoId, rendition, from, async percent =>
        {
            context.Heartbeat(percent);
            await videos.SetRenditionAsync(videoId, rendition, "running", percent, null, null, Ct);
            await progress.PublishAsync(videoId, Ct);
        }, Ct);
        await videos.SetRenditionAsync(videoId, rendition, "done", 100, size, null, Ct);
        await progress.PublishAsync(videoId, Ct);
        return size;
    }

    [Activity]
    public async Task PublishAsync(long videoId)
    {
        if (await videos.SetStatusAsync(videoId, [VideoStatus.Processing], VideoStatus.Published, null, Ct))
        {
            stats.Published();
        }
        await progress.PublishAsync(videoId, Ct);
    }

    // A rendition failed for good: delete every rendition object, mark the unfinished renditions
    // and the video failed.
    [Activity]
    public async Task FailAsync(long videoId, IReadOnlyList<string> renditions, string reason)
    {
        await blobs.DeleteAsync(renditions.Select(r => Blobs.RenditionKey(videoId, r)), Ct);
        await using (var conn = await videos.Db.OpenConnectionAsync(Ct))
        {
            await using var cmd = new Npgsql.NpgsqlCommand(
                "UPDATE renditions SET status = 'failed' WHERE video_id = $1 AND status <> 'failed'", conn);
            cmd.Parameters.AddWithValue(videoId);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        await videos.SetStatusAsync(videoId, [VideoStatus.Uploaded, VideoStatus.Processing], VideoStatus.Failed, reason, Ct);
        await progress.PublishAsync(videoId, Ct);
    }
}
