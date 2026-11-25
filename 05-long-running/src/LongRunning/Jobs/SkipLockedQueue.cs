using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LongRunning;

// Stages 2, 3 and 4: a queue that many workers can share safely.
//
// Stage 2: ClaimAsync. Claim the oldest queued job whose run_after has passed, in one transaction,
// so that two workers never get the same job and a worker never waits for another worker's row.
// Mark it running with locked_by = workerId, attempts + 1, and lease_until = now + LeaseSeconds.
// Goal (Stage2 tests): 8 workers claiming at once from 100 jobs claim each exactly once; a row that
// another transaction has locked is skipped, not waited for; nothing is claimable before run_after.
//
// Stage 3: leases and retries. A worker can die mid-job, or freeze and come back.
//   - ClaimAsync also takes running jobs whose lease_until has passed (their worker is presumed dead)
//   - ExtendLeaseAsync, CompleteAsync and FailAsync act only if the caller still holds the job:
//     same worker AND same attempt, still running. Otherwise they change nothing (and return false)
//   - FailAsync puts the job back in the queue with run_after = now + BackoffSeconds * 2^(attempt-1)
//     and the error in last_error
// The three methods below are the baseline's versions: they trust the caller. Make them check.
// Goal (Stage3 tests): an expired job is claimed again with attempt 2; a worker whose lease lapsed
// cannot complete it; a heartbeat keeps a job; a failed job comes back after its backoff, not before.
//
// Stage 4: dead letters. A job that fails on its MaxAttempts-th attempt is dead: FailAsync sets
// status 'dead' instead of queueing it again. ListDeadAsync shows dead jobs, oldest first.
// RequeueDeadAsync puts a dead job back (queued, attempts 0, run after now); false if it was not dead.
// Goal (Stage4 tests): a poison job is dead after MaxAttempts and never claimed again; requeueing it
// gives it a fresh set of attempts; a job that succeeds on its last attempt is done, not dead.
//
// Use the injected TimeProvider for every "now".
//
// Tests: dotnet test --filter-namespace "*Stage2" (then Stage3, Stage4)
public sealed class SkipLockedQueue(NpgsqlDataSource db, IOptions<JobsOptions> options, TimeProvider time) : IJobQueue
{
    private readonly TimeSpan _lease = TimeSpan.FromSeconds(options.Value.LeaseSeconds);
    private readonly TimeSpan _backoff = TimeSpan.FromSeconds(options.Value.BackoffSeconds);
    private readonly int _maxAttempts = options.Value.MaxAttempts;

    public Task<ClaimedJob?> ClaimAsync(string workerId, CancellationToken ct)
    {
        throw new NotImplementedException();
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

    public Task<IReadOnlyList<JobView>> ListDeadAsync(CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<bool> RequeueDeadAsync(long jobId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
