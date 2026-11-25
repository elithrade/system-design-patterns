using System.Collections.Concurrent;
using Npgsql;

namespace LongRunning.Tests.Stage2;

// Stage 2 goal: many workers share the queue; nobody runs a job twice or waits on another worker.
// Jobs are inserted directly, so these tests do not depend on your JobStore.
[Collection("infra")]
public sealed class ClaimTests(InfraFixture infra)
{
    [Fact]
    public async Task Eight_workers_claim_a_hundred_jobs_each_exactly_once()
    {
        await using var app = await App.StartAsync(infra, QueueKind.SkipLocked);
        for (var i = 0; i < 100; i++)
        {
            await JobsTable.InsertAsync(app.Db, app.Now, userId: 1 + i % 50);
        }
        var claims = new ConcurrentBag<ClaimedJob>();

        await Concurrently.RunAsync(8, async w =>
        {
            while (await app.Queue.ClaimAsync($"w{w}", CancellationToken.None) is { } job)
            {
                claims.Add(job);
            }
        });

        Assert.Equal(100, claims.Count);
        Assert.Equal(100, claims.Select(c => c.Id).Distinct().Count());
        foreach (var claim in claims.Take(10))
        {
            var row = await JobsTable.GetAsync(app.Db, claim.Id);
            Assert.Equal(("running", 1, claim.WorkerId), (row.Status, row.Attempts, row.LockedBy));
        }
    }

    [Fact]
    public async Task The_oldest_job_goes_first_with_a_lease()
    {
        await using var app = await App.StartAsync(infra, QueueKind.SkipLocked);
        var first = await JobsTable.InsertAsync(app.Db, app.Now);
        await JobsTable.InsertAsync(app.Db, app.Now);

        var job = await app.Queue.ClaimAsync("w1", CancellationToken.None);

        Assert.Equal((first, 1, "w1"), (job!.Id, job.Attempt, job.WorkerId));
        Assert.Equal(app.Now.UtcDateTime.AddSeconds(30), job.LeaseUntil);
        Assert.Equal(app.Now.UtcDateTime.AddSeconds(30), (await JobsTable.GetAsync(app.Db, first)).LeaseUntil);
    }

    [Fact]
    public async Task Nothing_is_claimed_before_its_run_after()
    {
        await using var app = await App.StartAsync(infra, QueueKind.SkipLocked);
        Assert.Null(await app.Queue.ClaimAsync("w1", CancellationToken.None));
        var later = await JobsTable.InsertAsync(app.Db, app.Now, runAfter: app.Now.AddMinutes(5));

        Assert.Null(await app.Queue.ClaimAsync("w1", CancellationToken.None));
        app.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(later, (await app.Queue.ClaimAsync("w1", CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task A_row_locked_by_someone_else_is_skipped_not_waited_for()
    {
        await using var app = await App.StartAsync(infra, QueueKind.SkipLocked);
        var locked = await JobsTable.InsertAsync(app.Db, app.Now);
        var free = await JobsTable.InsertAsync(app.Db, app.Now);
        await using var conn = new NpgsqlConnection(app.Db);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await new NpgsqlCommand($"SELECT 1 FROM jobs WHERE id = {locked} FOR UPDATE", conn, tx).ExecuteScalarAsync();

        var claim = app.Queue.ClaimAsync("w1", CancellationToken.None);
        var finished = await Task.WhenAny(claim, Task.Delay(2000)) == claim;
        await tx.RollbackAsync();

        Assert.True(finished, "the claim waited for another transaction's lock");
        Assert.Equal(free, (await claim)?.Id);
    }
}
