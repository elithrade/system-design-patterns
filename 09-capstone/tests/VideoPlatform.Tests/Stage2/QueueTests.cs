using Dapper;
using Npgsql;

namespace VideoPlatform.Tests.Stage2;

// Stage 2 goal: renditions as jobs. No worker loops run: the tests claim and run jobs by hand.
[Collection("infra")]
public sealed class QueueTests(InfraFixture infra)
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(App App, TranscodeQueue Queue, long Video)> StartAsync(InfraFixture infra, bool corrupt = false)
    {
        var app = await App.StartAsync(infra, Engine.Queue);
        var video = await app.UploadedVideoAsync(corrupt: corrupt);
        var queue = app.Get<TranscodeQueue>();
        await queue.EnqueueAsync(video, None);
        return (app, queue, video);
    }

    private static async Task<List<(string Name, string Status, int Attempts)>> RowsAsync(App app, long video)
    {
        await using var conn = new NpgsqlConnection(app.Db);
        return (await conn.QueryAsync<(string, string, int)>(
            "SELECT name, status, attempts FROM renditions WHERE video_id = @video ORDER BY name", new { video })).AsList();
    }

    [Fact]
    public async Task Enqueue_queues_each_rendition_once()
    {
        var (app, queue, video) = await StartAsync(infra);
        await using var _ = app;

        await queue.EnqueueAsync(video, None);

        Assert.Equal([("360p", "queued", 0), ("720p", "queued", 0), ("thumb", "queued", 0)], await RowsAsync(app, video));
        Assert.Equal(VideoStatus.Processing, (await app.SnapshotAsync(video)).Status);
    }

    [Fact]
    public async Task Running_every_job_publishes_the_video_once()
    {
        var (app, _, video) = await StartAsync(infra);
        await using var __ = app;
        var runner = app.Get<TranscodeRunner>();

        while (await runner.RunOnceAsync("w1", None))
        {
        }

        var snapshot = await app.SnapshotAsync(video);
        Assert.Equal(VideoStatus.Published, snapshot.Status);
        Assert.All(snapshot.Renditions, r => Assert.Equal(("done", 100), (r.Status, r.Percent)));
        Assert.Equal(1, app.Stats.PublishedCount);
        Assert.Equal(4, (await infra.KeysAsync(app.Bucket)).Count);
    }

    [Fact]
    public async Task Workers_never_claim_the_same_rendition()
    {
        var (app, queue, _) = await StartAsync(infra);
        await using var __ = app;

        var claims = await Concurrently.RunAsync(8, i => queue.ClaimAsync($"w{i}", None));

        var claimed = claims.Where(c => c is not null).Select(c => c!.Name).Order().ToList();
        Assert.Equal(App.Renditions, claimed);
    }

    [Fact]
    public async Task Two_last_renditions_finishing_together_publish_the_video_once()
    {
        await using var app = await App.StartAsync(infra, Engine.Queue);
        var queue = app.Get<TranscodeQueue>();
        for (var round = 0; round < 10; round++)
        {
            var video = await app.UploadedVideoAsync(bytes: 1000);
            await queue.EnqueueAsync(video, None);
            var jobs = new List<ClaimedRendition>();
            while (await queue.ClaimAsync($"w{jobs.Count}", None) is { } job)
            {
                jobs.Add(job);
            }
            Assert.Equal(CompleteResult.Done, await queue.CompleteAsync(jobs[0], 10, None));

            var results = await Concurrently.RunAsync(2, i => queue.CompleteAsync(jobs[i + 1], 10, None));

            Assert.Equal([CompleteResult.Done, CompleteResult.Published], results.Order());
            Assert.Equal(VideoStatus.Published, (await app.SnapshotAsync(video)).Status);
        }
    }

    [Fact]
    public async Task A_lapsed_lease_passes_the_rendition_on_and_the_first_worker_is_refused()
    {
        var (app, queue, _) = await StartAsync(infra);
        await using var __ = app;
        var first = (await queue.ClaimAsync("w1", None))!;
        Assert.True(await queue.ProgressAsync(first, 60, None));

        app.Time.Advance(TimeSpan.FromSeconds(App.LeaseSeconds + 1));
        var claims = new List<ClaimedRendition>();
        while (await queue.ClaimAsync("w2", None) is { } job)
        {
            claims.Add(job);
        }

        var second = Assert.Single(claims, c => c.Name == first.Name);
        Assert.Equal((2, 60), (second.Attempt, second.Percent));
        Assert.False(await queue.ProgressAsync(first, 70, None));
        Assert.Equal(CompleteResult.Rejected, await queue.CompleteAsync(first, 10, None));
        Assert.Equal(CompleteResult.Done, await queue.CompleteAsync(second, 10, None));
    }

    [Fact]
    public async Task Progress_keeps_the_lease()
    {
        var (app, queue, _) = await StartAsync(infra);
        await using var __ = app;
        var job = (await queue.ClaimAsync("w1", None))!;
        await queue.ClaimAsync("w1", None);
        await queue.ClaimAsync("w1", None);

        app.Time.Advance(TimeSpan.FromSeconds(App.LeaseSeconds - 5));
        Assert.True(await queue.ProgressAsync(job, 50, None));
        app.Time.Advance(TimeSpan.FromSeconds(10));

        // the other two lapsed (no progress), the one that reported did not
        var claims = new List<string>();
        while (await queue.ClaimAsync("w2", None) is { } other)
        {
            claims.Add(other.Name);
        }
        Assert.Equal(2, claims.Count);
        Assert.DoesNotContain(job.Name, claims);
    }

    [Fact]
    public async Task Failures_retry_with_growing_backoff_then_fail_the_video()
    {
        var (app, queue, video) = await StartAsync(infra);
        await using var __ = app;
        async Task<ClaimedRendition?> Claim720Async()
        {
            await using var conn = new NpgsqlConnection(app.Db);
            await conn.ExecuteAsync("UPDATE renditions SET run_after = run_after + interval '1 day' WHERE name <> '720p'");
            return await queue.ClaimAsync("w1", None);
        }

        var job = (await Claim720Async())!;
        Assert.False(await queue.FailAsync(job, "boom", permanent: false, None));
        Assert.Null(await Claim720Async());
        app.Time.Advance(TimeSpan.FromSeconds(App.BackoffSeconds));
        job = (await Claim720Async())!;
        Assert.Equal(2, job.Attempt);

        Assert.False(await queue.FailAsync(job, "boom", permanent: false, None));
        app.Time.Advance(TimeSpan.FromSeconds(App.BackoffSeconds));
        Assert.Null(await Claim720Async());
        app.Time.Advance(TimeSpan.FromSeconds(App.BackoffSeconds));
        job = (await Claim720Async())!;
        Assert.Equal(3, job.Attempt);

        Assert.True(await queue.FailAsync(job, "boom", permanent: false, None));
        var snapshot = await app.SnapshotAsync(video);
        Assert.Equal((VideoStatus.Failed, "boom"), (snapshot.Status, snapshot.Reason));
        Assert.Equal("failed", snapshot.Renditions.Single(r => r.Name == "720p").Status);
    }

    [Fact]
    public async Task A_corrupt_source_fails_the_video_at_once_and_its_other_renditions_never_run()
    {
        var (app, queue, video) = await StartAsync(infra, corrupt: true);
        await using var __ = app;

        Assert.True(await app.Get<TranscodeRunner>().RunOnceAsync("w1", None));

        Assert.Equal(VideoStatus.Failed, (await app.SnapshotAsync(video)).Status);
        Assert.Null(await queue.ClaimAsync("w1", None));
        Assert.Equal(1, app.Stats.Transcodes);
    }
}
