using System.Net;

namespace ScalingWrites.Tests.Stage1;

// Stage 1 goal: thousands of views cost one write, and none are lost or counted twice.
[Collection("infra")]
public sealed class BatchingTests(InfraFixture infra) : IAsyncLifetime
{
    private string _db = null!;
    private App _app = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await infra.CloneMainAsync();
        _app = App.Create(_db, CounterKind.Batched, extra: new() { ["Writes:FlushMilliseconds"] = "1000" });
        _app.CreateClient();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private BatchingViewCounter Counter => _app.Get<BatchingViewCounter>();

    [Fact]
    public async Task A_view_is_accepted_and_reaches_the_database_on_the_next_flush()
    {
        var client = _app.CreateClient();
        var before = await Sql.ViewsColumnAsync(_db, 3);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, await client.ViewAsync(3));
        }
        Assert.Equal(before, await Sql.ViewsColumnAsync(_db, 3));
        Assert.Equal(0, _app.Stats.CounterWrites);

        await Counter.FlushAsync(CancellationToken.None);

        Assert.Equal(before + 10, await Sql.ViewsColumnAsync(_db, 3));
    }

    [Fact]
    public async Task A_thousand_concurrent_views_cost_one_write()
    {
        var client = _app.CreateClient();
        var before = await Sql.ViewsColumnAsync(_db, 1);

        await Concurrently.RunAsync(1000, _ => client.ViewAsync(1));
        await Counter.FlushAsync(CancellationToken.None);

        Assert.Equal(before + 1000, await Sql.ViewsColumnAsync(_db, 1));
        Assert.Equal(1, _app.Stats.CounterWrites);
    }

    [Fact]
    public async Task Many_videos_still_cost_one_write_per_flush()
    {
        var before = new Dictionary<long, long>();
        for (long id = 1; id <= 50; id++)
        {
            before[id] = await Sql.ViewsColumnAsync(_db, id);
        }

        await Concurrently.RunAsync(500, i => Counter.RecordAsync(1 + i % 50, CancellationToken.None));
        await Counter.FlushAsync(CancellationToken.None);

        Assert.Equal(1, _app.Stats.CounterWrites);
        for (long id = 1; id <= 50; id++)
        {
            Assert.Equal(before[id] + 10, await Sql.ViewsColumnAsync(_db, id));
        }
    }

    [Fact]
    public async Task A_flush_with_nothing_buffered_writes_nothing()
    {
        await Counter.FlushAsync(CancellationToken.None);
        await Counter.FlushAsync(CancellationToken.None);

        Assert.Equal(0, _app.Stats.CounterWrites);
    }

    [Fact]
    public async Task Views_recorded_during_flushes_are_neither_lost_nor_doubled()
    {
        var counter = Counter;
        var before = await Sql.ViewsColumnAsync(_db, 2);
        const int writers = 8, each = 5000;

        var writing = Concurrently.RunAsync(writers, async _ =>
        {
            for (var i = 0; i < each; i++)
            {
                await counter.RecordAsync(2, CancellationToken.None);
            }
        });
        var flushes = 0;
        while (!writing.IsCompleted)
        {
            await counter.FlushAsync(CancellationToken.None);
            flushes++;
        }
        await writing;
        await counter.FlushAsync(CancellationToken.None);

        TestContext.Current.SendDiagnosticMessage($"{flushes} flushes overlapped the writers");
        Assert.Equal(before + writers * each, await Sql.ViewsColumnAsync(_db, 2));
    }

    [Fact]
    public async Task A_failed_flush_keeps_its_views_for_the_next_one()
    {
        var counter = Counter;
        var before = await Sql.ViewsColumnAsync(_db, 4);
        for (var i = 0; i < 10; i++)
        {
            await counter.RecordAsync(4, CancellationToken.None);
        }

        _app.Faults.FailNextWrites(1);
        await Assert.ThrowsAsync<InjectedFaultException>(() => counter.FlushAsync(CancellationToken.None));
        Assert.Equal(before, await Sql.ViewsColumnAsync(_db, 4));

        for (var i = 0; i < 5; i++)
        {
            await counter.RecordAsync(4, CancellationToken.None);
        }
        await counter.FlushAsync(CancellationToken.None);

        Assert.Equal(before + 15, await Sql.ViewsColumnAsync(_db, 4));
    }

    [Fact]
    public async Task The_flush_loop_writes_when_the_interval_passes()
    {
        var client = _app.CreateClient();
        var before = await Sql.ViewsColumnAsync(_db, 5);
        await client.ViewAsync(5);
        await client.ViewAsync(5);
        await client.ViewAsync(5);

        _app.Time.Advance(TimeSpan.FromMilliseconds(1000));

        await Eventually.AssertAsync(async () => Assert.Equal(before + 3, await Sql.ViewsColumnAsync(_db, 5)));
    }
}
