namespace Proximity.Tests.Stage2;

// Stage 2 goal: the same answers from PostGIS and a GiST index.
[Collection("infra")]
public sealed class PostGisTests(InfraFixture infra)
{
    [Fact]
    public Task The_search_agrees_with_the_scan() => IndexContract.SameAsTheScanAsync(infra, Engine.PostGis);

    [Fact]
    public Task With_a_limit_it_returns_the_nearest() => IndexContract.NearestFirstWithALimitAsync(infra, Engine.PostGis);

    [Fact]
    public Task A_moved_driver_is_found_at_the_new_place_only() => IndexContract.AMovedDriverIsFoundAtTheNewPlaceOnlyAsync(infra, Engine.PostGis);

    [Fact]
    public Task A_row_inserted_with_plain_sql_is_found() => IndexContract.ARowInsertedWithPlainSqlIsFoundAsync(infra, Engine.PostGis);

    [Fact]
    public Task Busy_drivers_are_not_offered() => IndexContract.BusyDriversAreNotOfferedAsync(infra, Engine.PostGis);

    [Fact]
    public Task It_is_five_times_faster_than_the_scan() => IndexContract.FiveTimesFasterThanTheScanAsync(infra, Engine.PostGis);

    // 5 km holds about 5,000 drivers. The rider wants the nearest 10: read 10, not 5,000.
    [Fact]
    public async Task A_wide_search_with_a_small_limit_stops_after_the_nearest()
    {
        await using var app = await App.StartAsync(infra, Engine.PostGis);
        var searches = Searches.Random(15, seed: 6).Select(s => s with { RadiusM = 5000 }).ToList();

        var scan = await Searches.MedianMsAsync(app.Scan, searches, limit: 10);
        var indexed = await Searches.MedianMsAsync(app.Index, searches, limit: 10);

        Assert.True(indexed * 5 <= scan, $"median {indexed:0.00} ms against the scan's {scan:0.00} ms: does the query sort every driver in range?");
    }
}
