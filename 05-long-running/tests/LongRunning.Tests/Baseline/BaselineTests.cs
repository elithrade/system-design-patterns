using System.Net;

namespace LongRunning.Tests.Baseline;

// Guards: the baseline works, slowly, with one worker. These pass from the start.
[Collection("infra")]
public sealed class BaselineTests(InfraFixture infra)
{
    [Fact]
    public async Task The_report_renders_inside_the_request()
    {
        await using var app = await App.StartAsync(infra, QueueKind.Naive);

        var response = await app.CreateClient().GetAsync("/users/7/year-in-review?year=2026");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task One_worker_runs_a_queued_job()
    {
        await using var app = await App.StartAsync(infra, QueueKind.Naive);
        var id = await JobsTable.InsertAsync(app.Db, app.Now, userId: 7);

        Assert.True(await app.Runner.RunOnceAsync("w1", CancellationToken.None));

        var row = await JobsTable.GetAsync(app.Db, id);
        Assert.Equal(("done", 1, true), (row.Status, row.Attempts, row.HasResult));
        Assert.False(await app.Runner.RunOnceAsync("w1", CancellationToken.None));
    }
}
