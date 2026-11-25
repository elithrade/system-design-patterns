using Microsoft.Extensions.Options;

namespace ScalingWrites;

// Stage 2. Spread a video's views over Shards rows, so concurrent views lock different rows.
//
// Write a migration, db/migrations/002_<name>.sql, that creates:
//
//     video_view_shards (video_id bigint, shard int, views bigint), primary key (video_id, shard)
//
// (the tests and db/seed.sql use these names). Then:
//   - RecordAsync adds 1 to one shard row of the video, creating the row if it is missing, and
//     returns Written. It must not update the videos row.
//   - GetViewsAsync returns video.Views (what the video had before sharding) plus the sum of its shards.
//
// The goal, which the tests check:
//   - every concurrent view counts
//   - a lock held on the videos row, or on one shard row, does not stop views from being written
//   - one video's views end up spread over most of its Shards rows
//
// Open connections with db.Main.OpenConnectionAsync and query with Dapper, as VideoRepository does.
// Calling stats.CounterWrite() and stats.CounterRead() per statement makes GET /stats comparable
// with the other stages; the tests do not check them.
//
// Tests: dotnet test --filter-namespace "*Stage2"
public sealed class ShardedViewCounter(Databases db, Stats stats, IOptions<WritesOptions> options) : IViewCounter
{
    private readonly int _shards = options.Value.Shards;

    public Task<RecordResult> RecordAsync(long videoId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<long> GetViewsAsync(Video video, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
