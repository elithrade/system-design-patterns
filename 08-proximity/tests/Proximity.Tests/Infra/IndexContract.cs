namespace Proximity.Tests;

// The behavior every Postgres-backed engine shares (stages 1 and 2 run these against their engine).
public static class IndexContract
{
    public static async Task SameAsTheScanAsync(InfraFixture infra, Engine engine)
    {
        await using var app = await App.StartAsync(infra, engine);
        foreach (var search in Searches.Random(30))
        {
            await Searches.AssertSameAsScanAsync(app, search);
        }
    }

    public static async Task NearestFirstWithALimitAsync(InfraFixture infra, Engine engine)
    {
        await using var app = await App.StartAsync(infra, engine);
        foreach (var search in Searches.Random(10, seed: 2))
        {
            await Searches.AssertNearestAsync(app, search with { RadiusM = 3000 }, limit: 5);
        }
    }

    public static async Task AMovedDriverIsFoundAtTheNewPlaceOnlyAsync(InfraFixture infra, Engine engine)
    {
        await using var app = await App.StartAsync(infra, engine);
        var before = await app.Index.NearbyAsync(40.70, -73.95, 3000, 1000, CancellationToken.None);
        var driver = before[0];

        // to Newark airport, outside the seeded box
        await app.Index.UpsertManyAsync([new DriverLocation(driver.DriverId, 40.6895, -74.1745, true)], CancellationToken.None);

        var there = await app.Index.NearbyAsync(40.6895, -74.1745, 200, 10, CancellationToken.None);
        var here = await app.Index.NearbyAsync(40.70, -73.95, 3000, 1000, CancellationToken.None);
        Assert.Equal([driver.DriverId], there.Select(d => d.DriverId));
        Assert.DoesNotContain(driver.DriverId, here.Select(d => d.DriverId));
    }

    public static async Task ARowInsertedWithPlainSqlIsFoundAsync(InfraFixture infra, Engine engine)
    {
        await using var app = await App.StartAsync(infra, engine);

        // White Plains, north of the seeded box
        await app.ExecuteAsync("INSERT INTO drivers (id, lat, lng, available, updated_at) VALUES (999999, 41.0340, -73.7629, true, now())");

        var found = await app.Index.NearbyAsync(41.0340, -73.7629, 100, 10, CancellationToken.None);
        Assert.Equal([999999L], found.Select(d => d.DriverId));
    }

    public static async Task BusyDriversAreNotOfferedAsync(InfraFixture infra, Engine engine)
    {
        await using var app = await App.StartAsync(infra, engine);
        var driver = (await app.Index.NearbyAsync(40.75, -73.98, 2000, 1, CancellationToken.None))[0];

        await app.Index.UpsertManyAsync([new DriverLocation(driver.DriverId, driver.Lat, driver.Lng, false)], CancellationToken.None);

        var found = await app.Index.NearbyAsync(driver.Lat, driver.Lng, 50, 100, CancellationToken.None);
        Assert.DoesNotContain(driver.DriverId, found.Select(d => d.DriverId));
    }

    public static async Task FiveTimesFasterThanTheScanAsync(InfraFixture infra, Engine engine)
    {
        await using var app = await App.StartAsync(infra, engine);
        var searches = Searches.Random(15, seed: 3).Select(s => s with { RadiusM = 500 }).ToList();

        var scan = await Searches.MedianMsAsync(app.Scan, searches);
        var indexed = await Searches.MedianMsAsync(app.Index, searches);

        Assert.True(indexed * 5 <= scan, $"median {indexed:0.00} ms against the scan's {scan:0.00} ms: is the index used?");
    }
}
