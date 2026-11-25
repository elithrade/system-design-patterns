using System.Diagnostics;
using System.Net;

namespace ScalingWrites.Tests.Stage4;

// Stage 4 goal: accept views fast into a bounded queue, write them in batches behind the response,
// and when the queue is full say no at once instead of making callers wait.
[Collection("infra")]
public sealed class QueueTests(InfraFixture infra)
{
    private async Task<(App App, string Db)> StartAsync(bool drainer, int capacity = 10_000, int batchSize = 1000)
    {
        var db = await infra.CloneMainAsync();
        var app = App.Create(db, CounterKind.Queued, extra: new()
        {
            ["Writes:Drainer"] = drainer.ToString(),
            ["Writes:QueueCapacity"] = capacity.ToString(),
            ["Writes:BatchSize"] = batchSize.ToString(),
        });
        app.CreateClient();
        return (app, db);
    }

    [Fact]
    public async Task Accepted_views_are_all_written_by_the_drainer()
    {
        var (app, db) = await StartAsync(drainer: true);
        await using var _ = app;
        var client = app.CreateClient();
        var before = await Sql.ViewsColumnAsync(db, 1);

        var codes = await Concurrently.RunAsync(2000, _ => client.ViewAsync(1));

        Assert.All(codes, c => Assert.Equal(HttpStatusCode.Accepted, c));
        await Eventually.AssertAsync(async () => Assert.Equal(before + 2000, await Sql.ViewsColumnAsync(db, 1)));
    }

    [Fact]
    public async Task A_full_queue_sheds_at_once()
    {
        var (app, _) = await StartAsync(drainer: false, capacity: 100);
        await using var __ = app;
        var client = app.CreateClient();

        var clock = Stopwatch.StartNew();
        // a TryEnqueue that waits for space never returns here, so give up after 5 s
        var responses = await Concurrently.RunAsync(300, _ => client.PostAsync("/videos/1/views", null))
            .WaitAsync(TimeSpan.FromSeconds(5));
        clock.Stop();

        Assert.Equal(100, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        var shed = responses.Where(r => r.StatusCode == HttpStatusCode.ServiceUnavailable).ToList();
        Assert.Equal(200, shed.Count);
        Assert.All(shed, r => Assert.True(r.Headers.RetryAfter is not null, "503 without Retry-After"));
        Assert.True(clock.ElapsedMilliseconds < 3000, $"300 requests took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(100, app.Get<ViewQueue>().Depth);
    }

    [Fact]
    public async Task Only_accepted_views_are_counted()
    {
        var (app, db) = await StartAsync(drainer: false, capacity: 100, batchSize: 1000);
        await using var _ = app;
        var client = app.CreateClient();
        var before = await Sql.ViewsColumnAsync(db, 2);
        await Concurrently.RunAsync(300, _ => client.ViewAsync(2));

        var drained = await app.Get<ViewQueue>().DrainOnceAsync(app.Get<VideoRepository>(), CancellationToken.None);

        Assert.Equal(100, drained);
        Assert.Equal(0, app.Get<ViewQueue>().Depth);
        Assert.Equal(before + 100, await Sql.ViewsColumnAsync(db, 2));
    }

    [Fact]
    public async Task One_drain_writes_a_batch_in_one_statement()
    {
        var (app, db) = await StartAsync(drainer: false, batchSize: 1000);
        await using var _ = app;
        var client = app.CreateClient();
        var before = new long[6];
        for (var id = 1; id <= 5; id++)
        {
            before[id] = await Sql.ViewsColumnAsync(db, id);
        }
        await Concurrently.RunAsync(500, i => client.ViewAsync(1 + i % 5));

        var drained = await app.Get<ViewQueue>().DrainOnceAsync(app.Get<VideoRepository>(), CancellationToken.None);

        Assert.Equal(500, drained);
        Assert.Equal(1, app.Stats.CounterWrites);
        for (var id = 1; id <= 5; id++)
        {
            Assert.Equal(before[id] + 100, await Sql.ViewsColumnAsync(db, id));
        }
    }

    [Fact]
    public async Task A_drain_takes_at_most_a_batch()
    {
        var (app, _) = await StartAsync(drainer: false, batchSize: 50);
        await using var __ = app;
        var client = app.CreateClient();
        await Concurrently.RunAsync(120, _ => client.ViewAsync(3));
        var queue = app.Get<ViewQueue>();

        Assert.Equal(50, await queue.DrainOnceAsync(app.Get<VideoRepository>(), CancellationToken.None));
        Assert.Equal(70, queue.Depth);
    }

    [Fact]
    public async Task A_drain_waits_for_the_first_view()
    {
        var (app, _) = await StartAsync(drainer: false);
        await using var __ = app;
        var queue = app.Get<ViewQueue>();

        var drain = queue.DrainOnceAsync(app.Get<VideoRepository>(), CancellationToken.None);
        await Task.Delay(200);
        Assert.False(drain.IsCompleted, "DrainOnceAsync returned with nothing queued");

        Assert.Equal(HttpStatusCode.Accepted, await app.CreateClient().ViewAsync(4));
        Assert.Equal(1, await drain.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
