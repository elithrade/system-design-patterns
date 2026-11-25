using Dapper;
using Npgsql;

namespace Proximity;

// How the app stores driver locations and finds nearby ones. Proximity:Engine picks one.
public interface IDriverIndex
{
    // Drivers report their location (the app sends a batch, or a batch of one).
    Task UpsertManyAsync(IReadOnlyList<DriverLocation> drivers, CancellationToken ct);

    // Available drivers within radiusM of the point, nearest first, at most limit of them.
    Task<IReadOnlyList<NearbyDriver>> NearbyAsync(double lat, double lng, double radiusM, int limit, CancellationToken ct);
}

// The baseline: compute the distance from the rider to every available driver, keep the close ones.
public sealed class ScanIndex(NpgsqlDataSource db, DriverStore store) : IDriverIndex
{
    public Task UpsertManyAsync(IReadOnlyList<DriverLocation> drivers, CancellationToken ct) => store.UpsertManyAsync(drivers, ct);

    public async Task<IReadOnlyList<NearbyDriver>> NearbyAsync(double lat, double lng, double radiusM, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<NearbyDriver>(new CommandDefinition("""
            SELECT driver_id, lat, lng, distance_m FROM (
                SELECT id AS driver_id, lat, lng, haversine_m(@lat, @lng, lat, lng) AS distance_m FROM drivers WHERE available
            ) d
            WHERE distance_m <= @radiusM
            ORDER BY distance_m
            LIMIT @limit
            """, new { lat, lng, radiusM, limit }, cancellationToken: ct))).AsList();
    }
}
