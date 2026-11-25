namespace LongRunning;

public static class JobKinds
{
    public const string YearInReview = "year-in-review";
    public const string Summary = "summary";
}

// A job as GET /jobs/{id} shows it. Times are UTC.
public sealed record JobView(
    long Id,
    string Kind,
    long UserId,
    int Year,
    string Status,
    int Attempts,
    DateTime RunAfter,
    string? LastError,
    DateTime CreatedAt,
    DateTime? FinishedAt);

// A job a worker has claimed. Attempt is jobs.attempts after the claim (1 for the first run);
// LeaseUntil is when the claim lapses unless the worker extends it.
public sealed record ClaimedJob(long Id, string Kind, long UserId, int Year, int Attempt, string WorkerId, DateTime LeaseUntil);

// How workers take jobs and report back. Jobs:Queue picks the implementation.
public interface IJobQueue
{
    // The oldest job that may run now, marked running for workerId. Null if there is none.
    Task<ClaimedJob?> ClaimAsync(string workerId, CancellationToken ct);

    // Called every LeaseSeconds / 3 while the job runs. False: the worker no longer holds it.
    Task<bool> ExtendLeaseAsync(ClaimedJob job, CancellationToken ct);

    // Stores the result. False: the worker no longer held the job, and nothing was stored.
    Task<bool> CompleteAsync(ClaimedJob job, byte[] result, CancellationToken ct);

    // The job threw.
    Task FailAsync(ClaimedJob job, string error, CancellationToken ct);
}
