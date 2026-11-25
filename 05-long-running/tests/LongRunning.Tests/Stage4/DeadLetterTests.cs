using System.Net;
using System.Net.Http.Json;

namespace LongRunning.Tests.Stage4;

// Stage 4 goal: a job that keeps failing stops being retried and waits for a person.
[Collection("infra")]
public sealed class DeadLetterTests(InfraFixture infra)
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const long Poison = 666;

    private static Task<App> StartAsync(InfraFixture infra) =>
        App.StartAsync(infra, QueueKind.SkipLocked, new() { ["Jobs:MaxAttempts"] = "3", ["Reports:PoisonUserId"] = Poison.ToString() });

    // Runs whatever is claimable, then moves past any backoff.
    private static async Task RunAndWaitAsync(App app, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await app.Runner.RunOnceAsync("w1", None);
            app.Time.Advance(TimeSpan.FromMinutes(1));
        }
    }

    [Fact]
    public async Task A_poison_job_is_dead_after_max_attempts_and_never_runs_again()
    {
        await using var app = await StartAsync(infra);
        var id = await JobsTable.InsertAsync(app.Db, app.Now, userId: Poison);

        await RunAndWaitAsync(app, 3);

        var row = await JobsTable.GetAsync(app.Db, id);
        Assert.Equal(("dead", 3), (row.Status, row.Attempts));
        Assert.Contains("cannot draw", row.LastError);
        app.Time.Advance(TimeSpan.FromDays(1));
        Assert.False(await app.Runner.RunOnceAsync("w1", None));
        Assert.Equal(3, app.Stats.Renders);
    }

    [Fact]
    public async Task Dead_jobs_are_listed_oldest_first()
    {
        await using var app = await StartAsync(infra);
        var first = await JobsTable.InsertAsync(app.Db, app.Now, userId: Poison);
        var second = await JobsTable.InsertAsync(app.Db, app.Now, userId: Poison, kind: JobKinds.YearInReview);
        await JobsTable.InsertAsync(app.Db, app.Now, userId: 7);
        await RunAndWaitAsync(app, 7);

        var dead = await app.CreateClient().GetFromJsonAsync<List<JobView>>("/jobs/dead");

        Assert.Equal([first, second], dead!.Select(j => j.Id));
        Assert.All(dead, j => Assert.Equal("dead", j.Status));
    }

    [Fact]
    public async Task Requeueing_a_dead_job_gives_it_fresh_attempts()
    {
        await using var app = await StartAsync(infra);
        var id = await JobsTable.InsertAsync(app.Db, app.Now, userId: Poison);
        await RunAndWaitAsync(app, 3);
        var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/jobs/{id}/retry", null)).StatusCode);

        var row = await JobsTable.GetAsync(app.Db, id);
        Assert.Equal(("queued", 0, app.Now.UtcDateTime), (row.Status, row.Attempts, row.RunAfter));
        var claimed = await app.Queue.ClaimAsync("w1", None);
        Assert.Equal((id, 1), (claimed!.Id, claimed.Attempt));
    }

    [Fact]
    public async Task Only_dead_jobs_can_be_requeued()
    {
        await using var app = await StartAsync(infra);
        var queued = await JobsTable.InsertAsync(app.Db, app.Now, userId: 7);
        var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/jobs/{queued}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/jobs/999999/retry", null)).StatusCode);
    }

    [Fact]
    public async Task Success_on_the_last_attempt_is_done_not_dead()
    {
        await using var app = await StartAsync(infra);
        var id = await JobsTable.InsertAsync(app.Db, app.Now, userId: 7);
        app.Faults.FailNextRenders(2);

        await RunAndWaitAsync(app, 3);

        var row = await JobsTable.GetAsync(app.Db, id);
        Assert.Equal(("done", 3, true), (row.Status, row.Attempts, row.HasResult));
    }
}
