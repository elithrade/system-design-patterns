using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Proximity;

// Stages 3 and 4: driver locations in Redis, not Postgres.
//
// Stage 3:
//   - UpsertManyAsync: available drivers go into the geo set {KeyPrefix}drivers:geo (GEOADD);
//     a driver who is not available leaves it. A batch should cost about one round trip, not one
//     per driver (IBatch, or the array overloads)
//   - NearbyAsync: GEOSEARCH from the rider's point, by radius, nearest first, at most limit,
//     with distances in meters
// Redis stores positions as 52-bit geohashes and uses its own Earth radius, so results can differ
// from the baseline's by a meter or so: the tests allow 5 m.
//
// Stage 4: drivers who stop reporting (app closed, phone died) must stop being offered.
//   - also record when each driver last reported, on the injected TimeProvider, in the sorted set
//     {KeyPrefix}drivers:seen (score: Unix time in seconds)
//   - NearbyAsync leaves out drivers not seen for StaleSeconds or more
//   - PruneAsync removes those drivers from both sets and returns how many it removed. The app
//     runs it from POST /admin/prune; production would run it on a timer
//
// Tests: dotnet test --filter-namespace "*Stage3" (then Stage4)
public sealed class RedisDriverIndex(IConnectionMultiplexer redis, TimeProvider time, IOptions<ProximityOptions> options) : IDriverIndex
{
    private readonly RedisKey _geo = options.Value.KeyPrefix + "drivers:geo";
    private readonly RedisKey _seen = options.Value.KeyPrefix + "drivers:seen";
    private readonly TimeSpan _stale = TimeSpan.FromSeconds(options.Value.StaleSeconds);

    public Task UpsertManyAsync(IReadOnlyList<DriverLocation> drivers, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<IReadOnlyList<NearbyDriver>> NearbyAsync(double lat, double lng, double radiusM, int limit, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<long> PruneAsync(CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
