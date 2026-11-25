namespace LongRunning;

// The "Reports" section: how long the work takes.
public sealed class ReportsOptions
{
    public const string Section = "Reports";

    // A year-in-review PDF. 40 s by default: longer than nginx's 30 s timeout.
    public int RenderMilliseconds { get; set; } = 40_000;

    // A summary card: the kind of job that is fast enough to do inline.
    public int SummaryMilliseconds { get; set; } = 200;

    // Rendering this user's report always throws (stage 4's poison job).
    public long PoisonUserId { get; set; } = 666;
}

public enum QueueKind
{
    // the baseline: read a queued job, then mark it running
    Naive,
    // stages 2-4: your queue
    SkipLocked,
}

// The "Jobs" section. Set from the command line with Jobs__Queue=SkipLocked etc.
public sealed class JobsOptions
{
    public const string Section = "Jobs";

    public QueueKind Queue { get; set; } = QueueKind.Naive;

    // Worker loops in this process. 0: none (the tests run jobs by hand).
    public int Workers { get; set; } = 4;

    // How long an idle worker waits before asking for a job again.
    public int PollMilliseconds { get; set; } = 500;

    // Stage 3: how long a claim lasts without a heartbeat.
    public int LeaseSeconds { get; set; } = 30;

    // Stage 3: the wait before retry n is BackoffSeconds * 2^(n-1).
    public int BackoffSeconds { get; set; } = 2;

    // Stage 4: attempts before a job is dead.
    public int MaxAttempts { get; set; } = 5;
}
