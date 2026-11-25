using Npgsql;

namespace LongRunning;

// Stage 1. The API side of the queue: put a job in, look at it, fetch its result.
//
// EnqueueAsync inserts a queued job (run_after = now, created_at = now, from the injected
// TimeProvider) and returns its id. If the same user already has a queued or running job of the
// same kind for the same year, it returns that job's id instead: a user who clicks Generate five
// times gets one job. That must hold when the five clicks arrive at once, so write a migration,
// db/migrations/002_<name>.sql, that lets the database enforce it (a partial unique index), and
// use it from your INSERT.
// GetAsync returns the job, or null. GetResultAsync returns the result of a done job, else null.
//
// The goal, which the tests check:
//   - POST answers 202 at once, however long the report takes, with the job's id
//   - after a worker runs it, the job is done and its result is the PDF
//   - 20 identical POSTs at once make one job; another year or another user makes another;
//     once a job is done, a new POST makes a new job
//
// Query with Dapper on db, as Renderer does.
//
// Tests: dotnet test --filter-namespace "*Stage1"
public sealed class JobStore(NpgsqlDataSource db, TimeProvider time)
{
    public Task<long> EnqueueAsync(string kind, long userId, int year, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<JobView?> GetAsync(long jobId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<byte[]?> GetResultAsync(long jobId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
