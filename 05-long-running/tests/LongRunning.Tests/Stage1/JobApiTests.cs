using System.Diagnostics;
using System.Net;

namespace LongRunning.Tests.Stage1;

// Stage 1 goal: the request returns a job id at once; the work happens elsewhere.
[Collection("infra")]
public sealed class JobApiTests(InfraFixture infra)
{
    [Fact]
    public async Task Post_answers_at_once_however_long_the_report_takes()
    {
        await using var app = await App.StartAsync(infra, QueueKind.Naive, new() { ["Reports:RenderMilliseconds"] = "60000" });
        var client = app.CreateClient();

        var clock = Stopwatch.StartNew();
        var job = await client.EnqueueReportAsync(7);
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 2000, $"POST took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(("queued", JobKinds.YearInReview, 7L, 2026), (job.Status, job.Kind, job.UserId, job.Year));
        Assert.Equal(app.Now.UtcDateTime, job.CreatedAt);
    }

    [Fact]
    public async Task After_a_worker_runs_it_the_result_is_the_pdf()
    {
        await using var app = await App.StartAsync(infra, QueueKind.Naive);
        var client = app.CreateClient();
        var job = await client.EnqueueReportAsync(7);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/jobs/{job.Id}/result")).StatusCode);

        await app.Runner.RunOnceAsync("w1", CancellationToken.None);

        Assert.Equal("done", (await client.JobAsync(job.Id)).Status);
        var result = await client.GetAsync($"/jobs/{job.Id}/result");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("application/pdf", result.Content.Headers.ContentType?.MediaType);
        var pdf = System.Text.Encoding.ASCII.GetString(await result.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("%PDF-1.4", pdf);
        Assert.Contains("runner 7", pdf);
    }

    [Fact]
    public async Task Twenty_identical_clicks_at_once_make_one_job()
    {
        await using var app = await App.StartAsync(infra, QueueKind.Naive);
        var client = app.CreateClient();

        var jobs = await Concurrently.RunAsync(20, _ => client.EnqueueReportAsync(7));

        Assert.Single(jobs.Select(j => j.Id).Distinct());
        Assert.Equal(1L, await CountJobsAsync(app));
    }

    [Fact]
    public async Task Another_year_or_user_is_another_job()
    {
        await using var app = await App.StartAsync(infra, QueueKind.Naive);
        var client = app.CreateClient();

        var a = await client.EnqueueReportAsync(7, 2026);
        var b = await client.EnqueueReportAsync(7, 2025);
        var c = await client.EnqueueReportAsync(8, 2026);
        var summary = await client.PostAsync("/users/7/summary?year=2026", null);

        Assert.Equal(3, new[] { a.Id, b.Id, c.Id }.Distinct().Count());
        Assert.Equal(HttpStatusCode.Accepted, summary.StatusCode);
        Assert.Equal(4L, await CountJobsAsync(app));
    }

    [Fact]
    public async Task Once_a_job_is_done_a_new_click_makes_a_new_job()
    {
        await using var app = await App.StartAsync(infra, QueueKind.Naive);
        var client = app.CreateClient();
        var first = await client.EnqueueReportAsync(7);
        await app.Runner.RunOnceAsync("w1", CancellationToken.None);

        var second = await client.EnqueueReportAsync(7);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("queued", second.Status);
    }

    [Fact]
    public async Task An_unknown_job_is_not_found()
    {
        await using var app = await App.StartAsync(infra, QueueKind.Naive);
        var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/jobs/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/jobs/999999/result")).StatusCode);
    }

    private static Task<long> CountJobsAsync(App app) =>
        new Npgsql.NpgsqlConnection(app.Db) is var conn
            ? Dapper.SqlMapper.ExecuteScalarAsync<long>(conn, "SELECT count(*) FROM jobs")
            : Task.FromResult(0L);
}
