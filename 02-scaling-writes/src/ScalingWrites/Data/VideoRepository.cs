using Dapper;

namespace ScalingWrites;

// A video as the main database stores it. Views is the videos.views column, which is not the
// whole count once stage 2 or 3 keeps views elsewhere.
public sealed record Video(long Id, string Title, long Views);

// What GET /videos/{id} and GET /videos/top return.
public sealed record VideoViews(long Id, long Views);

// The main database.
public sealed class VideoRepository(Databases db, Stats stats, Faults faults)
{
    public async Task<Video?> GetAsync(long id, CancellationToken ct)
    {
        stats.CounterRead();
        await using var conn = await db.Main.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Video>(new CommandDefinition(
            "SELECT id, title, views FROM videos WHERE id = @id", new { id }, cancellationToken: ct));
    }

    // The baseline write: one statement per view, on the video's row. False if there is no such video.
    public async Task<bool> AddViewAsync(long id, CancellationToken ct)
    {
        stats.CounterWrite();
        await using var conn = await db.Main.OpenConnectionAsync(ct);
        var updated = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE videos SET views = views + 1 WHERE id = @id", new { id }, cancellationToken: ct));
        return updated == 1;
    }

    // Adds counts[id] to each video's views in ONE statement, whatever the number of videos.
    // Ids with no video are ignored. Throws if Faults says so (the tests use that), and then
    // nothing is written.
    public async Task AddViewsAsync(IReadOnlyDictionary<long, long> counts, CancellationToken ct)
    {
        stats.CounterWrite();
        faults.BeforeWrite();
        await using var conn = await db.Main.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE videos v SET views = v.views + d.n
            FROM unnest(@ids, @ns) AS d(id, n)
            WHERE v.id = d.id
            """,
            new { ids = counts.Keys.ToArray(), ns = counts.Values.ToArray() },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<VideoViews>> TopAsync(int limit, CancellationToken ct)
    {
        stats.CounterRead();
        await using var conn = await db.Main.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<VideoViews>(new CommandDefinition(
            "SELECT id, views FROM videos ORDER BY views DESC, id LIMIT @limit", new { limit }, cancellationToken: ct));
        return rows.AsList();
    }
}
