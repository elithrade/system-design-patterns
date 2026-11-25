using System.Diagnostics;

namespace ScalingWrites.Tests.Stage3;

// Stage 3 goal, part 2: the app writes each video's count to one partition, and the top list
// gathers from all of them at once.
[Collection("infra")]
public sealed class PartitionTests(InfraFixture infra) : IAsyncLifetime
{
    private List<string> _partitions = null!;
    private App _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _partitions = [await infra.ClonePartitionAsync(), await infra.ClonePartitionAsync(), await infra.ClonePartitionAsync()];
        _app = App.Create(await infra.CloneMainAsync(), CounterKind.Partitioned, _partitions,
            new() { ["Writes:Partitioner"] = "Ring", ["Writes:VirtualNodes"] = "100" });
        _client = _app.CreateClient();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task A_videos_views_land_on_the_partition_the_ring_picks()
    {
        await Concurrently.RunAsync(20, _ => _client.ViewAsync(7));

        var expected = int.Parse(new ConsistentHashRing(["p0", "p1", "p2"], 100).NodeFor(7)[1..]);
        for (var p = 0; p < 3; p++)
        {
            var views = await Sql.ScalarAsync<long>(_partitions[p], "SELECT coalesce(sum(views), 0) FROM view_counts WHERE video_id = 7");
            Assert.Equal(p == expected ? 20 : 0, views);
        }
        Assert.Equal(20, await _client.ViewsAsync(7));
    }

    [Fact]
    public async Task Videos_spread_over_all_three_partitions()
    {
        await Concurrently.RunAsync(300, i => _client.ViewAsync(1 + i));

        for (var p = 0; p < 3; p++)
        {
            var rows = await Sql.ScalarAsync<long>(_partitions[p], "SELECT count(*) FROM view_counts");
            Assert.True(rows is >= 60 and <= 140, $"partition {p} has {rows} of 300 videos");
        }
    }

    [Fact]
    public async Task Top_is_the_global_top_across_partitions()
    {
        // video i gets i views; video 40 ties with video 30
        var views = Enumerable.Range(1, 30).SelectMany(i => Enumerable.Repeat((long)i, i))
            .Concat(Enumerable.Repeat(40L, 30))
            .ToArray();
        await Concurrently.RunAsync(views.Length, i => _client.ViewAsync(views[i]));

        var top = await _client.TopAsync(6);

        Assert.Equal(
            [new(30, 30), new(40, 30), new(29, 29), new(28, 28), new(27, 27), new VideoViews(26, 26)],
            top);
    }

    [Fact]
    public async Task Top_asks_the_partitions_at_the_same_time()
    {
        await _client.ViewAsync(1);
        _app.Faults.PartitionReadDelay = TimeSpan.FromMilliseconds(500);

        var clock = Stopwatch.StartNew();
        await _client.TopAsync(10);
        clock.Stop();

        for (var p = 0; p < 3; p++)
        {
            Assert.Equal(1, _app.Stats.PartitionReads(p));
        }
        Assert.True(clock.ElapsedMilliseconds < 1200, $"top took {clock.ElapsedMilliseconds} ms with three 500 ms partitions");
    }
}
