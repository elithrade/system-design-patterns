namespace Proximity.Tests.Baseline;

// Guards: the baseline scan is correct (it is the reference for every stage). These pass from the start.
[Collection("infra")]
public sealed class ScanTests(InfraFixture infra)
{
    [Fact]
    public async Task The_scan_finds_exactly_the_available_drivers_in_range_nearest_first()
    {
        await using var app = await App.StartAsync(infra, Engine.Scan);
        var search = new Search(40.72, -73.99, 800);

        var found = await app.Index.NearbyAsync(search.Lat, search.Lng, search.RadiusM, 1000, CancellationToken.None);

        var expected = (await app.Store.AllAsync(InfraFixture.Drivers, CancellationToken.None).FirstAsync())
            .Where(d => d.Available)
            .Select(d => (d.DriverId, Distance: Geo.DistanceM(search.Lat, search.Lng, d.Lat, d.Lng)))
            .Where(d => d.Distance <= search.RadiusM)
            .OrderBy(d => d.Distance)
            .Select(d => d.DriverId);
        Assert.Equal(expected, found.Select(d => d.DriverId));
        Assert.NotEmpty(found);
    }

    [Fact]
    public async Task Reports_move_drivers()
    {
        await using var app = await App.StartAsync(infra, Engine.Scan);

        await app.Index.UpsertManyAsync([new DriverLocation(1, 40.6895, -74.1745, true)], CancellationToken.None);

        var found = await app.Index.NearbyAsync(40.6895, -74.1745, 100, 10, CancellationToken.None);
        Assert.Equal([1L], found.Select(d => d.DriverId));
    }

    [Fact]
    public async Task A_batch_with_two_reports_from_one_driver_keeps_the_last()
    {
        await using var app = await App.StartAsync(infra, Engine.Scan);

        await app.Index.UpsertManyAsync([new DriverLocation(1, 40.70, -73.95, true), new DriverLocation(1, 40.6895, -74.1745, true)], CancellationToken.None);

        var found = await app.Index.NearbyAsync(40.6895, -74.1745, 100, 10, CancellationToken.None);
        Assert.Equal([1L], found.Select(d => d.DriverId));
    }
}
