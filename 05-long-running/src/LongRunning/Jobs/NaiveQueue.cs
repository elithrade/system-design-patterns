using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LongRunning;

// The baseline queue. Correct with one worker. With several, two of them can read the same queued
// job before either marks it running, and both run it. A worker that dies leaves its job running
// forever, and a job that fails once stays failed.
public sealed class NaiveQueue(NpgsqlDataSource db, IOptions<JobsOptions> options, TimeProvider time) : IJobQueue
{
    private readonly TimeSpan _lease = TimeSpan.FromSeconds(options.Value.LeaseSeconds);

    public async Task<ClaimedJob?> ClaimAsync(string workerId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var now = time.GetUtcNow();
        var id = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM jobs WHERE status = 'queued' AND run_after <= @now ORDER BY id LIMIT 1",
            new { now }, cancellationToken: ct));
        if (id is null)
        {
            return null;
        }
        return await conn.QuerySingleAsync<ClaimedJob>(new CommandDefinition("""
            UPDATE jobs SET status = 'running', locked_by = @workerId, attempts = attempts + 1, lease_until = @leaseUntil
            WHERE id = @id
            RETURNING id, kind, user_id, year, attempts AS attempt, locked_by AS worker_id, lease_until
            """, new { id, workerId, leaseUntil = now + _lease }, cancellationToken: ct));
    }

    public async Task<bool> ExtendLeaseAsync(ClaimedJob job, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE jobs SET lease_until = @leaseUntil WHERE id = @id",
            new { job.Id, leaseUntil = time.GetUtcNow() + _lease }, cancellationToken: ct));
        return true;
    }

    public async Task<bool> CompleteAsync(ClaimedJob job, byte[] result, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE jobs SET status = 'done', result = @result, finished_at = @now, locked_by = NULL, lease_until = NULL WHERE id = @id",
            new { job.Id, result, now = time.GetUtcNow() }, cancellationToken: ct));
        return true;
    }

    public async Task FailAsync(ClaimedJob job, string error, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE jobs SET status = 'failed', last_error = @error, finished_at = @now, locked_by = NULL, lease_until = NULL WHERE id = @id",
            new { job.Id, error, now = time.GetUtcNow() }, cancellationToken: ct));
    }
}
