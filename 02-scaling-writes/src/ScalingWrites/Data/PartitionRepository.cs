using Dapper;

namespace ScalingWrites;

// The partition databases (stage 3). Each call names the partition it goes to.
public sealed class PartitionRepository(Databases db, Stats stats, Faults faults)
{
    public int Count => db.Partitions.Count;

    public async Task AddViewAsync(int partition, long videoId, CancellationToken ct)
    {
        stats.PartitionWrite(partition);
        await using var conn = await db.Partitions[partition].OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO view_counts (video_id, views) VALUES (@videoId, 1)
            ON CONFLICT (video_id) DO UPDATE SET views = view_counts.views + 1
            """, new { videoId }, cancellationToken: ct));
    }

    // 0 if the partition has no row for the video.
    public async Task<long> GetViewsAsync(int partition, long videoId, CancellationToken ct)
    {
        await BeforeReadAsync(partition, ct);
        await using var conn = await db.Partitions[partition].OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT views FROM view_counts WHERE video_id = @videoId", new { videoId }, cancellationToken: ct)) ?? 0;
    }

    // The partition's own top videos, most viewed first (ties by id).
    public async Task<IReadOnlyList<VideoViews>> TopAsync(int partition, int limit, CancellationToken ct)
    {
        await BeforeReadAsync(partition, ct);
        await using var conn = await db.Partitions[partition].OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<VideoViews>(new CommandDefinition(
            "SELECT video_id AS id, views FROM view_counts ORDER BY views DESC, video_id LIMIT @limit",
            new { limit }, cancellationToken: ct));
        return rows.AsList();
    }

    private async Task BeforeReadAsync(int partition, CancellationToken ct)
    {
        stats.PartitionRead(partition);
        if (faults.PartitionReadDelay > TimeSpan.Zero)
        {
            await Task.Delay(faults.PartitionReadDelay, ct);
        }
    }
}
