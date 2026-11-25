using Npgsql;

namespace Proximity;

// Stage 1, part 2: find nearby drivers with a geohash prefix query.
//
//   - write a migration, db/migrations/002_<name>.sql, that gives every driver row a geohash and
//     indexes it for prefix searches. Every row needs it, including rows inserted by plain SQL
//     (the seed and the tests insert that way), and it must follow every update: a generated
//     column (ST_GeoHash is there) or a trigger does that. A btree index serves a prefix search
//     when the column compares bytes (COLLATE "C") or uses text_pattern_ops
//   - NearbyAsync: pick a precision whose cells are at least radiusM across, take the rider's
//     cell and its 8 neighbors (Geohash), fetch the available drivers in those 9 cells through the
//     index, then keep those within radiusM, nearest first, at most limit (haversine_m in SQL,
//     or Geo.DistanceM)
// The tests compare your results with the baseline's on random searches, and check the search is
// at least 5 times faster than the baseline on 100,000 drivers.
//
// Tests: dotnet test --filter-namespace "*Stage1"
public sealed class GeohashIndex(NpgsqlDataSource db, DriverStore store) : IDriverIndex
{
    public Task UpsertManyAsync(IReadOnlyList<DriverLocation> drivers, CancellationToken ct) => store.UpsertManyAsync(drivers, ct);

    public Task<IReadOnlyList<NearbyDriver>> NearbyAsync(double lat, double lng, double radiusM, int limit, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
