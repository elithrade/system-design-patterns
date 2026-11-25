namespace ScalingWrites;

// Stage 3's counter: a video's count lives in view_counts on the partition the partitioner picks.
// It starts at 0: moving the existing videos.views counts over is a question in the README.
// A view of a video that does not exist still writes a row; checking would cost a read on the main
// database for every view.
public sealed class PartitionedViewCounter(PartitionRepository partitions, IPartitioner partitioner) : IViewCounter
{
    public async Task<RecordResult> RecordAsync(long videoId, CancellationToken ct)
    {
        await partitions.AddViewAsync(partitioner.PartitionFor(videoId), videoId, ct);
        return RecordResult.Written;
    }

    public Task<long> GetViewsAsync(Video video, CancellationToken ct) =>
        partitions.GetViewsAsync(partitioner.PartitionFor(video.Id), video.Id, ct);
}
