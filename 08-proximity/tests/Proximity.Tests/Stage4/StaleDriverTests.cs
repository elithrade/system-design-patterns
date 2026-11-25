using StackExchange.Redis;

namespace Proximity.Tests.Stage4;

// Stage 4 goal: drivers who stop reporting disappear from searches, and from Redis.
[Collection("infra")]
public sealed class StaleDriverTests(InfraFixture infra)
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static DriverLocation At(long id, double lat = 40.75, double lng = -73.98, bool available = true) => new(id, lat, lng, available);

    private static async Task<App> StartAsync(InfraFixture infra)
    {
        var app = await App.StartAsync(infra, Engine.Redis);
        // Redis starts empty for this prefix: only the drivers each test reports exist
        return app;
    }

    private static Task<IReadOnlyList<NearbyDriver>> NearAsync(App app) => app.Index.NearbyAsync(40.75, -73.98, 1000, 100, None);

    [Fact]
    public async Task A_driver_who_stops_reporting_is_not_offered()
    {
        await using var app = await StartAsync(infra);
        await app.Index.UpsertManyAsync([At(1), At(2, lat: 40.7505)], None);

        app.Time.Advance(TimeSpan.FromSeconds(App.StaleSeconds - 10));
        await app.Index.UpsertManyAsync([At(2, lat: 40.7505)], None);
        Assert.Equal([1L, 2L], (await NearAsync(app)).Select(d => d.DriverId).Order());

        app.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal([2L], (await NearAsync(app)).Select(d => d.DriverId));
    }

    [Fact]
    public async Task Reporting_again_brings_a_driver_back()
    {
        await using var app = await StartAsync(infra);
        await app.Index.UpsertManyAsync([At(1)], None);
        app.Time.Advance(TimeSpan.FromSeconds(App.StaleSeconds + 5));
        Assert.Empty(await NearAsync(app));

        await app.Index.UpsertManyAsync([At(1)], None);

        Assert.Equal([1L], (await NearAsync(app)).Select(d => d.DriverId));
    }

    [Fact]
    public async Task Prune_removes_stale_drivers_from_redis_and_keeps_fresh_ones()
    {
        await using var app = await StartAsync(infra);
        var index = (RedisDriverIndex)app.Index;
        var redis = infra.Redis.GetDatabase();
        await app.Index.UpsertManyAsync(Enumerable.Range(1, 50).Select(i => At(i, lat: 40.75 + i * 0.0001)).ToList(), None);
        app.Time.Advance(TimeSpan.FromSeconds(App.StaleSeconds));
        await app.Index.UpsertManyAsync(Enumerable.Range(1, 10).Select(i => At(i, lat: 40.75 + i * 0.0001)).ToList(), None);

        Assert.Equal(40, await index.PruneAsync(None));

        Assert.Equal(10, await redis.SortedSetLengthAsync($"{app.Prefix}drivers:geo"));
        Assert.Equal(10, await redis.SortedSetLengthAsync($"{app.Prefix}drivers:seen"));
        Assert.Equal(10, (await NearAsync(app)).Count);
        Assert.Equal(0, await index.PruneAsync(None));
    }

    [Fact]
    public async Task Busy_drivers_leave_the_geo_set_at_once()
    {
        await using var app = await StartAsync(infra);
        await app.Index.UpsertManyAsync([At(1), At(2)], None);

        await app.Index.UpsertManyAsync([At(1, available: false)], None);

        Assert.Equal(1, await infra.Redis.GetDatabase().SortedSetLengthAsync($"{app.Prefix}drivers:geo"));
        Assert.Equal([2L], (await NearAsync(app)).Select(d => d.DriverId));
    }
}
