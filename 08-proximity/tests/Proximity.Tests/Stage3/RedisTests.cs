namespace Proximity.Tests.Stage3;

// Stage 3 goal: the same answers from Redis. Each test keeps drivers 1-20,000 (in Postgres, for
// the reference scan) and loads them into Redis through your UpsertManyAsync.
[Collection("infra")]
public sealed class RedisTests(InfraFixture infra)
{
    private const long Kept = 20_000;

    private async Task<App> StartAsync()
    {
        var app = await App.StartAsync(infra, Engine.Redis);
        await app.ExecuteAsync("DELETE FROM drivers WHERE id > @Kept", new { Kept });
        await app.LoadIndexAsync();
        return app;
    }

    [Fact]
    public async Task The_search_agrees_with_the_scan()
    {
        await using var app = await StartAsync();
        foreach (var search in Searches.Random(30))
        {
            await Searches.AssertSameAsScanAsync(app, search);
        }
    }

    [Fact]
    public async Task With_a_limit_it_returns_the_nearest()
    {
        await using var app = await StartAsync();
        foreach (var search in Searches.Random(10, seed: 2))
        {
            await Searches.AssertNearestAsync(app, search with { RadiusM = 3000 }, limit: 5);
        }
    }

    [Fact]
    public async Task A_moved_driver_is_found_at_the_new_place_only()
    {
        await using var app = await StartAsync();
        var driver = (await app.Index.NearbyAsync(40.70, -73.95, 3000, 1, CancellationToken.None))[0];

        await app.Index.UpsertManyAsync([new DriverLocation(driver.DriverId, 40.6895, -74.1745, true)], CancellationToken.None);

        var there = await app.Index.NearbyAsync(40.6895, -74.1745, 200, 10, CancellationToken.None);
        var here = await app.Index.NearbyAsync(driver.Lat, driver.Lng, 50, 100, CancellationToken.None);
        Assert.Equal([driver.DriverId], there.Select(d => d.DriverId));
        Assert.DoesNotContain(driver.DriverId, here.Select(d => d.DriverId));
    }

    [Fact]
    public async Task A_driver_who_becomes_busy_is_not_offered()
    {
        await using var app = await StartAsync();
        var driver = (await app.Index.NearbyAsync(40.75, -73.98, 2000, 1, CancellationToken.None))[0];

        await app.Index.UpsertManyAsync([new DriverLocation(driver.DriverId, driver.Lat, driver.Lng, false)], CancellationToken.None);

        var found = await app.Index.NearbyAsync(driver.Lat, driver.Lng, 50, 100, CancellationToken.None);
        Assert.DoesNotContain(driver.DriverId, found.Select(d => d.DriverId));
    }

    [Fact]
    public async Task Nowhere_near_anyone_finds_nobody()
    {
        await using var app = await StartAsync();

        Assert.Empty(await app.Index.NearbyAsync(51.5074, -0.1278, 5000, 10, CancellationToken.None));
    }
}
