using Dapper;
using Npgsql;

namespace VideoPlatform;

public static class VideoStatus
{
    public const string Pending = "pending";
    public const string Uploaded = "uploaded";
    public const string Processing = "processing";
    public const string Published = "published";
    public const string Failed = "failed";
    public const string Abandoned = "abandoned";

    public static bool IsFinal(string status) => status is Published or Failed or Abandoned;
}

public sealed record Video(long Id, long OwnerId, string Title, string ContentType, long Size, string Status, string? Reason,
    long Version, DateTime CreatedAt);

public sealed record RenditionProgress(string Name, string Status, int Percent);

// A video's whole state at one version: what GET /videos/{id} returns and what a progress event
// carries. Progress is state, not a log: a newer snapshot replaces every older one.
public sealed record VideoSnapshot(long VideoId, long Version, string Status, string? Reason, IReadOnlyList<RenditionProgress> Renditions)
{
    public bool IsFinal => VideoStatus.IsFinal(Status);
}

// Test hooks: code that runs right after the next snapshot read, before its caller sees it.
public sealed class ReadHooks
{
    private Func<Task>? _afterNextSnapshot;

    public void AfterNextSnapshot(Func<Task> hook) => _afterNextSnapshot = hook;

    internal Task RunAfterSnapshotAsync() => Interlocked.Exchange(ref _afterNextSnapshot, null)?.Invoke() ?? Task.CompletedTask;
}

// The videos and renditions tables.
public sealed class VideoStore(NpgsqlDataSource db, TimeProvider time, ReadHooks hooks)
{
    public NpgsqlDataSource Db => db;

    public async Task<Video> CreateAsync(long ownerId, string title, string contentType, long size, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<Video>(new CommandDefinition("""
            INSERT INTO videos (owner_id, title, content_type, size, created_at) VALUES (@ownerId, @title, @contentType, @size, @now)
            RETURNING *
            """, new { ownerId, title, contentType, size, now = time.GetUtcNow() }, cancellationToken: ct));
    }

    public async Task<Video?> GetAsync(long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Video>(new CommandDefinition("SELECT * FROM videos WHERE id = @id", new { id }, cancellationToken: ct));
    }

    // The video and its renditions, read in one statement so they belong to one version.
    public async Task<VideoSnapshot?> SnapshotAsync(long id, CancellationToken ct)
    {
        VideoSnapshot? snapshot;
        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            var rows = (await conn.QueryAsync<(long Version, string Status, string? Reason, string? Name, string? RStatus, int? Percent)>(new CommandDefinition("""
                SELECT v.version, v.status, v.reason, r.name, r.status, r.percent
                FROM videos v LEFT JOIN renditions r ON r.video_id = v.id
                WHERE v.id = @id
                ORDER BY r.name
                """, new { id }, cancellationToken: ct))).AsList();
            snapshot = rows.Count == 0
                ? null
                : new VideoSnapshot(id, rows[0].Version, rows[0].Status, rows[0].Reason,
                    rows.Where(r => r.Name is not null).Select(r => new RenditionProgress(r.Name!, r.RStatus!, r.Percent!.Value)).ToList());
        }
        await hooks.RunAfterSnapshotAsync();
        return snapshot;
    }

    // Moves the video to status if it is in one of from. False if it was not.
    public async Task<bool> SetStatusAsync(long id, string[] from, string status, string? reason, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE videos SET status = @status, reason = @reason WHERE id = @id AND status = ANY(@from)",
            new { id, from, status, reason }, cancellationToken: ct)) == 1;
    }

    // One queued row per rendition name; existing rows are left alone.
    public async Task CreateRenditionsAsync(long id, IEnumerable<string> names, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO renditions (video_id, name, run_after) SELECT @id, name, @now FROM unnest(@names) AS name
            ON CONFLICT DO NOTHING
            """, new { id, names = names.ToArray(), now = time.GetUtcNow() }, cancellationToken: ct));
    }

    // Records a rendition's progress or outcome (the inline baseline and Temporal's activities;
    // the queue engine updates its rows itself).
    public async Task SetRenditionAsync(long id, string name, string status, int percent, long? size, string? error, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE renditions SET status = @status, percent = @percent, size = coalesce(@size, size), last_error = coalesce(@error, last_error)
            WHERE video_id = @id AND name = @name
            """, new { id, name, status, percent, size, error }, cancellationToken: ct));
    }
}
