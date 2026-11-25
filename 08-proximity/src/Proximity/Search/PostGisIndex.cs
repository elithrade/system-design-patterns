using Npgsql;

namespace Proximity;

// Stage 2: let PostGIS do it.
//
//   - write a migration, db/migrations/003_<name>.sql, that gives every driver row a
//     geography(Point, 4326) and a GiST index on it. As in stage 1, it must be there for rows
//     inserted by plain SQL and follow every update
//   - NearbyAsync: drivers within radiusM (ST_DWithin), nearest first, at most limit, through the
//     index. Distances on the sphere (use_spheroid false), so they agree with haversine_m. The
//     <-> operator orders by distance through a GiST index, so with a limit of 10 the index can
//     stop after 10 rows
// Same tests as stage 1 (results equal to the baseline's, at least 5 times faster at 500 m), plus a
// 5 km search with a limit of 10 that must also be 5 times faster than the baseline. If that one
// fails, read the plan: EXPLAIN (ANALYZE) your query for a 5 km search. Does it sort thousands of
// rows to return 10?
//
// Tests: dotnet test --filter-namespace "*Stage2"
public sealed class PostGisIndex(NpgsqlDataSource db, DriverStore store) : IDriverIndex
{
    public Task UpsertManyAsync(IReadOnlyList<DriverLocation> drivers, CancellationToken ct) => store.UpsertManyAsync(drivers, ct);

    public Task<IReadOnlyList<NearbyDriver>> NearbyAsync(double lat, double lng, double radiusM, int limit, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
