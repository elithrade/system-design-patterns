namespace LongRunning.Tests.Stage3;

// Stage 3 goal: a dead worker's job runs again, a frozen worker cannot clobber the new run, and a
// failed job comes back after a growing wait.
[Collection("infra")]
public sealed class LeaseAndRetryTests(InfraFixture infra)
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task An_expired_lease_is_claimed_again()
    {
        await using var app = await App.StartAsync(infra, QueueKind.SkipLocked);
        var id = await JobsTable.InsertAsync(app.Db, app.Now);
        await app.Queue.ClaimAsync("dead-worker", None);

        app.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(await app.Queue.ClaimAsync("w2", None));

        app.Time.Advance(TimeSpan.FromSeconds(21));
        var again = await app.Queue.ClaimAsync("w2", None);

        Assert.Equal((id, 2, "w2"), (again!.Id, again.Attempt, again.WorkerId));
    }

    [Fact]
    public async Task A_worker_that_lost_its_lease_cannot_complete_or_fail_the_job()
    {
        await using var app = await App.StartAsync(infra, QueueKind.SkipLocked);
        var id = await JobsTable.InsertAsync(app.Db, app.Now);
        var frozen = (await app.Queue.ClaimAsync("frozen", None))!;
        app.Time.Advance(TimeSpan.FromSeconds(31));
        var fresh = (await app.Queue.ClaimAsync("fresh", None))!;

        Assert.False(await app.Queue.CompleteAsync(frozen, [1, 2, 3], None));
        await app.Queue.FailAsync(frozen, "too late", None);
        Assert.False(await app.Queue.ExtendLeaseAsync(frozen, None));

        var row = await JobsTable.GetAsync(app.Db, id);
        Assert.Equal(("running", "fresh", false), (row.Status, row.LockedBy, row.HasResult));
        Assert.True(await app.Queue.CompleteAsync(fresh, [4, 5, 6], None));
        Assert.Equal("done", (await JobsTable.GetAsync(app.Db, id)).Status);
    }

    [Fact]
    public async Task A_heartbeat_keeps_the_job()
    {
        await using var app = await App.StartAsync(infra, QueueKind.SkipLocked);
        var id = await JobsTable.InsertAsync(app.Db, app.Now);
        var job = (await app.Queue.ClaimAsync("w1", None))!;

        app.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.True(await app.Queue.ExtendLeaseAsync(job, None));
        Assert.Equal(app.Now.UtcDateTime.AddSeconds(30), (await JobsTable.GetAsync(app.Db, id)).LeaseUntil);

        app.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.Null(await app.Queue.ClaimAsync("w2", None));
        Assert.True(await app.Queue.CompleteAsync(job, [1], None));
    }

    [Fact]
    public async Task A_failed_job_comes_back_after_a_growing_backoff()
    {
        await using var app = await App.StartAsync(infra, QueueKind.SkipLocked);
        var id = await JobsTable.InsertAsync(app.Db, app.Now);

        await app.Queue.FailAsync((await app.Queue.ClaimAsync("w1", None))!, "tile server timed out", None);
        var row = await JobsTable.GetAsync(app.Db, id);
        Assert.Equal(("queued", "tile server timed out", app.Now.UtcDateTime.AddSeconds(2)), (row.Status, row.LastError, row.RunAfter));
        Assert.Null(row.LockedBy);

        app.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await app.Queue.ClaimAsync("w1", None));
        app.Time.Advance(TimeSpan.FromSeconds(1));
        var second = (await app.Queue.ClaimAsync("w1", None))!;
        Assert.Equal(2, second.Attempt);

        await app.Queue.FailAsync(second, "again", None);
        Assert.Equal(app.Now.UtcDateTime.AddSeconds(4), (await JobsTable.GetAsync(app.Db, id)).RunAfter);
    }

    [Fact]
    public async Task Completing_twice_only_counts_once()
    {
        await using var app = await App.StartAsync(infra, QueueKind.SkipLocked);
        var id = await JobsTable.InsertAsync(app.Db, app.Now);
        var job = (await app.Queue.ClaimAsync("w1", None))!;

        Assert.True(await app.Queue.CompleteAsync(job, [1], None));
        Assert.False(await app.Queue.CompleteAsync(job, [2], None));

        var row = await JobsTable.GetAsync(app.Db, id);
        Assert.Equal(("done", true), (row.Status, row.HasResult));
    }

    [Fact]
    public async Task The_runner_retries_a_transient_failure()
    {
        await using var app = await App.StartAsync(infra, QueueKind.SkipLocked);
        var id = await JobsTable.InsertAsync(app.Db, app.Now, userId: 7);
        app.Faults.FailNextRenders(1);

        await app.Runner.RunOnceAsync("w1", None);
        var failed = await JobsTable.GetAsync(app.Db, id);
        Assert.Equal(("queued", 1), (failed.Status, failed.Attempts));
        Assert.Contains("timed out", failed.LastError);

        app.Time.Advance(TimeSpan.FromSeconds(2));
        await app.Runner.RunOnceAsync("w1", None);

        Assert.Equal(("done", 2), ((await JobsTable.GetAsync(app.Db, id)).Status, (await JobsTable.GetAsync(app.Db, id)).Attempts));
    }
}
