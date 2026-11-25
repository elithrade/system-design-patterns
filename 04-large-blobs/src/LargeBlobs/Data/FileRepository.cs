using Dapper;
using Npgsql;

namespace LargeBlobs;

public sealed record FileRecord(
    long Id,
    long OwnerId,
    string Name,
    string ContentType,
    long Size,
    string Status,
    string? UploadId,
    // UTC; Npgsql reads timestamptz as a DateTime with Kind = Utc
    DateTime CreatedAt,
    DateTime? ReadyAt)
{
    public string Key => Blobs.KeyFor(Id);
}

// File metadata in Postgres. Times come from the injected TimeProvider, so the tests can age a row.
public sealed class FileRepository(NpgsqlDataSource db, TimeProvider time)
{
    static FileRepository() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private const string Columns = "id, owner_id, name, content_type, size, status, upload_id, created_at, ready_at";

    public async Task<long> InsertAsync(long ownerId, string name, string contentType, long size, string status, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var now = time.GetUtcNow();
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO files (owner_id, name, content_type, size, status, created_at, ready_at)
            VALUES (@ownerId, @name, @contentType, @size, @status, @now, CASE WHEN @status = 'ready' THEN @now END)
            RETURNING id
            """, new { ownerId, name, contentType, size, status, now }, cancellationToken: ct));
    }

    public async Task<FileRecord?> GetAsync(long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<FileRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM files WHERE id = @id", new { id }, cancellationToken: ct));
    }

    // Of these ids, the ones that have a row.
    public async Task<HashSet<long>> ExistingAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var found = await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT id FROM files WHERE id = ANY(@ids)", new { ids = ids.ToArray() }, cancellationToken: ct));
        return found.ToHashSet();
    }

    // pending -> ready. False if the file was not pending (already ready, failed, or gone).
    public async Task<bool> MarkReadyAsync(long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE files SET status = 'ready', ready_at = @now, upload_id = NULL WHERE id = @id AND status = 'pending'",
            new { id, now = time.GetUtcNow() }, cancellationToken: ct)) == 1;
    }

    // pending -> failed. False if the file was not pending.
    public async Task<bool> MarkFailedAsync(long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE files SET status = 'failed', upload_id = NULL WHERE id = @id AND status = 'pending'",
            new { id }, cancellationToken: ct)) == 1;
    }

    public async Task SetUploadIdAsync(long id, string? uploadId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE files SET upload_id = @uploadId WHERE id = @id", new { id, uploadId }, cancellationToken: ct));
    }

    // Pending files created before cutoff, oldest first.
    public async Task<IReadOnlyList<FileRecord>> PendingBeforeAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<FileRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM files WHERE status = 'pending' AND created_at < @cutoff ORDER BY created_at",
            new { cutoff }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM files WHERE id = @id", new { id }, cancellationToken: ct)) == 1;
    }
}
