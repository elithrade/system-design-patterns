using Dapper;
using Npgsql;

namespace Proximity;

// A driver's report: where they are and whether they take rides.
public sealed record DriverLocation(long DriverId, double Lat, double Lng, bool Available);

// A search result. DistanceM is from the rider.
public sealed record NearbyDriver(long DriverId, double Lat, double Lng, double DistanceM);

// The drivers table.
public sealed class DriverStore(NpgsqlDataSource db, TimeProvider time)
{
    // Insert or update many drivers in one statement.
    public async Task UpsertManyAsync(IReadOnlyList<DriverLocation> reports, CancellationToken ct)
    {
        // One row per driver (the last report wins): ON CONFLICT DO UPDATE fails if a statement
        // touches a row twice. In id order: two batches that lock the same rows in different orders
        // deadlock.
        var drivers = reports.GroupBy(d => d.DriverId).Select(g => g.Last()).OrderBy(d => d.DriverId).ToList();
        if (drivers.Count == 0)
        {
            return;
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO drivers (id, lat, lng, available, updated_at)
            SELECT id, lat, lng, available, @now FROM unnest(@ids, @lats, @lngs, @available) AS t(id, lat, lng, available)
            ON CONFLICT (id) DO UPDATE SET lat = EXCLUDED.lat, lng = EXCLUDED.lng, available = EXCLUDED.available, updated_at = EXCLUDED.updated_at
            """, new
            {
                ids = drivers.Select(d => d.DriverId).ToArray(),
                lats = drivers.Select(d => d.Lat).ToArray(),
                lngs = drivers.Select(d => d.Lng).ToArray(),
                available = drivers.Select(d => d.Available).ToArray(),
                now = time.GetUtcNow(),
            }, cancellationToken: ct));
    }

    // Every driver, in pages of pageSize, ordered by id (to copy them into Redis).
    public async IAsyncEnumerable<IReadOnlyList<DriverLocation>> AllAsync(int pageSize, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        long after = 0;
        while (true)
        {
            List<DriverLocation> page;
            await using (var conn = await db.OpenConnectionAsync(ct))
            {
                page = (await conn.QueryAsync<DriverLocation>(new CommandDefinition(
                    "SELECT id AS driver_id, lat, lng, available FROM drivers WHERE id > @after ORDER BY id LIMIT @pageSize",
                    new { after, pageSize }, cancellationToken: ct))).AsList();
            }
            if (page.Count == 0)
            {
                yield break;
            }
            yield return page;
            after = page[^1].DriverId;
        }
    }
}
