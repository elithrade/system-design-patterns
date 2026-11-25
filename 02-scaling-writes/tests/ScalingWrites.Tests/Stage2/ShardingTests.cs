using System.Net;
using Npgsql;

namespace ScalingWrites.Tests.Stage2;

// Stage 2 goal: one video's views land on several rows, so no single row lock serializes them.
[Collection("infra")]
public sealed class ShardingTests(InfraFixture infra) : IAsyncLifetime
{
    private const int Shards = 8;
    private string _db = null!;
    private App _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await infra.CloneMainAsync();
        _app = App.Create(_db, CounterKind.Sharded, extra: new() { ["Writes:Shards"] = Shards.ToString() });
        _client = _app.CreateClient();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task A_seeded_video_keeps_its_count()
    {
        Assert.Equal(await Sql.ViewsColumnAsync(_db, 5), await _client.ViewsAsync(5));
    }

    [Fact]
    public async Task Concurrent_views_all_count()
    {
        var before = await _client.ViewsAsync(1);

        var codes = await Concurrently.RunAsync(500, _ => _client.ViewAsync(1));

        Assert.All(codes, c => Assert.Equal(HttpStatusCode.NoContent, c));
        Assert.Equal(before + 500, await _client.ViewsAsync(1));
    }

    [Fact]
    public async Task Views_get_past_a_lock_on_the_videos_row()
    {
        var column = await Sql.ViewsColumnAsync(_db, 7);
        await using var conn = new NpgsqlConnection(_db);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // the lock UPDATE videos SET views = ... takes (a foreign key check does not conflict with it)
        await new NpgsqlCommand("SELECT 1 FROM videos WHERE id = 7 FOR NO KEY UPDATE", conn, tx).ExecuteScalarAsync();

        var views = Concurrently.RunAsync(50, _ => _client.ViewAsync(7));
        var finished = await Task.WhenAny(views, Task.Delay(5000)) == views;
        await tx.CommitAsync();

        Assert.True(finished, "views waited on the videos row lock");
        Assert.Equal(column, await Sql.ViewsColumnAsync(_db, 7));
        Assert.Equal(column + 50, await _client.ViewsAsync(7));
    }

    [Fact]
    public async Task One_videos_views_spread_over_most_shards()
    {
        await Concurrently.RunAsync(400, _ => _client.ViewAsync(6));

        var rows = await Sql.ScalarAsync<long>(_db, "SELECT count(*) FROM video_view_shards WHERE video_id = 6");
        var sum = await Sql.ScalarAsync<long>(_db, "SELECT sum(views) FROM video_view_shards WHERE video_id = 6");

        Assert.InRange(rows, 6, Shards);
        Assert.Equal(400, sum);
    }

    [Fact]
    public async Task A_locked_shard_row_does_not_stop_the_others()
    {
        await Concurrently.RunAsync(200, _ => _client.ViewAsync(8));
        var before = await _client.ViewsAsync(8);

        await using var conn = new NpgsqlConnection(_db);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await new NpgsqlCommand(
            "SELECT 1 FROM video_view_shards WHERE video_id = 8 ORDER BY shard LIMIT 1 FOR UPDATE", conn, tx)
            .ExecuteScalarAsync();

        var views = Enumerable.Range(0, 40).Select(_ => _client.ViewAsync(8)).ToArray();
        await Task.Delay(2000);
        var done = views.Count(v => v.IsCompleted);
        await tx.CommitAsync();
        await Task.WhenAll(views);

        Assert.True(done >= 25, $"only {done} of 40 views finished while one shard row was locked");
        Assert.Equal(before + 40, await _client.ViewsAsync(8));
    }
}
