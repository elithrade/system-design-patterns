using Dapper;
using Npgsql;

namespace Proximity.Tests.Stage1;

// Stage 1 goal: geohashes that agree with PostGIS, and a prefix search that agrees with the scan.
[Collection("infra")]
public sealed class GeohashTests(InfraFixture infra)
{
    [Fact]
    public void Encode_matches_the_known_example()
    {
        Assert.Equal("u4pruydqqvj", Geohash.Encode(57.64911, 10.40744, 11));
        Assert.Equal("u4pru", Geohash.Encode(57.64911, 10.40744, 5));
    }

    [Fact]
    public async Task Encode_agrees_with_postgis_on_random_points()
    {
        await using var app = await App.StartAsync(infra, Engine.Scan);
        var random = new Random(4);
        var points = Enumerable.Range(0, 300)
            .Select(i => (Lat: random.NextDouble() * 170 - 85, Lng: random.NextDouble() * 358 - 179, Precision: 1 + i % 12))
            .ToList();
        await using var conn = new NpgsqlConnection(app.Db);

        foreach (var (lat, lng, precision) in points)
        {
            var expected = await conn.ExecuteScalarAsync<string>(
                "SELECT ST_GeoHash(ST_SetSRID(ST_MakePoint(@lng, @lat), 4326), @precision)", new { lat, lng, precision });
            Assert.True(expected == Geohash.Encode(lat, lng, precision), $"({lat}, {lng}) at {precision}: expected {expected}, got {Geohash.Encode(lat, lng, precision)}");
        }
    }

    [Fact]
    public async Task Neighbors_are_the_eight_cells_touching_the_cell()
    {
        await using var app = await App.StartAsync(infra, Engine.Scan);
        var random = new Random(5);
        await using var conn = new NpgsqlConnection(app.Db);

        for (var i = 0; i < 60; i++)
        {
            // precision 3 and up: coarser cells reach across the poles or the antimeridian
            var cell = Geohash.Encode(random.NextDouble() * 120 - 60, random.NextDouble() * 340 - 170, 3 + i % 7);
            var neighbors = Geohash.Neighbors(cell);

            Assert.Equal(8, neighbors.Distinct().Count());
            Assert.DoesNotContain(cell, neighbors);
            foreach (var neighbor in neighbors)
            {
                Assert.Equal(cell.Length, neighbor.Length);
                var touches = await conn.ExecuteScalarAsync<bool>(
                    "SELECT ST_Touches(ST_GeomFromGeoHash(@cell), ST_GeomFromGeoHash(@neighbor))", new { cell, neighbor });
                Assert.True(touches, $"{neighbor} does not touch {cell}");
            }
        }
    }

    [Fact]
    public Task The_search_agrees_with_the_scan() => IndexContract.SameAsTheScanAsync(infra, Engine.Geohash);

    [Fact]
    public Task With_a_limit_it_returns_the_nearest() => IndexContract.NearestFirstWithALimitAsync(infra, Engine.Geohash);

    [Fact]
    public Task A_moved_driver_is_found_at_the_new_place_only() => IndexContract.AMovedDriverIsFoundAtTheNewPlaceOnlyAsync(infra, Engine.Geohash);

    [Fact]
    public Task A_row_inserted_with_plain_sql_is_found() => IndexContract.ARowInsertedWithPlainSqlIsFoundAsync(infra, Engine.Geohash);

    [Fact]
    public Task Busy_drivers_are_not_offered() => IndexContract.BusyDriversAreNotOfferedAsync(infra, Engine.Geohash);

    [Fact]
    public Task It_is_five_times_faster_than_the_scan() => IndexContract.FiveTimesFasterThanTheScanAsync(infra, Engine.Geohash);
}
