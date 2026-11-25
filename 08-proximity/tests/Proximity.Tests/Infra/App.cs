using System.Diagnostics;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Proximity.Tests;

// The app under test, on its own cloned database (100,000 drivers) and Redis key prefix, with a
// fake clock.
public sealed class App : WebApplicationFactory<Program>
{
    public const int StaleSeconds = 30;

    static App() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private readonly Dictionary<string, string?> _settings;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));
    public string Db { get; }
    public string Prefix { get; }

    private App(Dictionary<string, string?> settings, string db, string prefix)
    {
        _settings = settings;
        Db = db;
        Prefix = prefix;
    }

    public static async Task<App> StartAsync(InfraFixture infra, Engine engine)
    {
        var db = await infra.CloneAsync();
        var prefix = $"t{Guid.NewGuid():N}:";
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Rides"] = db,
            ["ConnectionStrings:Redis"] = infra.RedisConnectionString,
            ["Proximity:Engine"] = engine.ToString(),
            ["Proximity:KeyPrefix"] = prefix,
            ["Proximity:StaleSeconds"] = StaleSeconds.ToString(),
            // the fixture migrated the template already
            ["Db:Migrate"] = "false",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        var app = new App(settings, db, prefix);
        _ = app.Server;
        return app;
    }

    public IDriverIndex Index => Services.GetRequiredService<IDriverIndex>();
    public DriverStore Store => Services.GetRequiredService<DriverStore>();

    // The baseline on the same database: the reference answer.
    public ScanIndex Scan => new(Services.GetRequiredService<NpgsqlDataSource>(), Store);

    public async Task ExecuteAsync(string sql, object? args = null)
    {
        await using var conn = new NpgsqlConnection(Db);
        await conn.ExecuteAsync(sql, args);
    }

    public async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var conn = new NpgsqlConnection(Db);
        return await conn.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("no value");
    }

    // Copies drivers (all of them, or those up to maxId) from Postgres into the index.
    public async Task LoadIndexAsync(long maxId = long.MaxValue)
    {
        await foreach (var page in Store.AllAsync(5000, CancellationToken.None))
        {
            var batch = page.Where(d => d.DriverId <= maxId).ToList();
            await Index.UpsertManyAsync(batch, CancellationToken.None);
            if (batch.Count < page.Count)
            {
                return;
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // not Development: no developer exception page
        builder.UseEnvironment("Test");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time)));
    }
}

public sealed record Search(double Lat, double Lng, double RadiusM);

public static class Searches
{
    // Allowed disagreement with the baseline, in meters (Redis rounds positions to ~0.6 m and uses
    // a slightly different Earth radius).
    public const double ToleranceM = 5;

    // Random searches inside the seeded box, with radii from 300 m to 3 km.
    public static List<Search> Random(int count, int seed = 1)
    {
        var random = new Random(seed);
        double[] radii = [300, 800, 1500, 3000];
        return Enumerable.Range(0, count)
            .Select(_ => new Search(40.58 + random.NextDouble() * 0.29, -74.07 + random.NextDouble() * 0.29, radii[random.Next(radii.Length)]))
            .ToList();
    }

    // The engine finds every driver the baseline finds within radius - 5 m, nothing beyond
    // radius + 5 m, nearest first, with correct distances.
    public static async Task AssertSameAsScanAsync(App app, Search search, int limit = 5000)
    {
        var found = await app.Index.NearbyAsync(search.Lat, search.Lng, search.RadiusM, limit, CancellationToken.None);
        var inner = await app.Scan.NearbyAsync(search.Lat, search.Lng, search.RadiusM - ToleranceM, limit, CancellationToken.None);
        var outer = await app.Scan.NearbyAsync(search.Lat, search.Lng, search.RadiusM + ToleranceM, limit, CancellationToken.None);
        Assert.True(outer.Count < limit, "the search returned the limit; raise it");

        var foundIds = found.Select(d => d.DriverId).ToHashSet();
        var missing = inner.Where(d => !foundIds.Contains(d.DriverId)).ToList();
        Assert.True(missing.Count == 0, $"{search}: missed {missing.Count} of {inner.Count} drivers, e.g. {missing.FirstOrDefault()}");
        var outerIds = outer.Select(d => d.DriverId).ToHashSet();
        var extra = found.Where(d => !outerIds.Contains(d.DriverId)).ToList();
        Assert.True(extra.Count == 0, $"{search}: returned {extra.Count} drivers that are too far or unavailable, e.g. {extra.FirstOrDefault()}");
        foreach (var d in found)
        {
            var real = Geo.DistanceM(search.Lat, search.Lng, d.Lat, d.Lng);
            Assert.True(Math.Abs(real - d.DistanceM) <= ToleranceM, $"driver {d.DriverId}: distance {d.DistanceM:0.0} m, really {real:0.0} m");
        }
        for (var i = 1; i < found.Count; i++)
        {
            Assert.True(found[i].DistanceM >= found[i - 1].DistanceM - ToleranceM, $"{search}: results are not nearest first");
        }
    }

    // With a small limit: the nearest ones.
    public static async Task AssertNearestAsync(App app, Search search, int limit)
    {
        var found = await app.Index.NearbyAsync(search.Lat, search.Lng, search.RadiusM, limit, CancellationToken.None);
        var expected = await app.Scan.NearbyAsync(search.Lat, search.Lng, search.RadiusM, limit, CancellationToken.None);
        Assert.Equal(limit, expected.Count);
        Assert.Equal(limit, found.Count);
        var farthestAllowed = expected[^1].DistanceM + ToleranceM;
        Assert.All(found, d => Assert.True(Geo.DistanceM(search.Lat, search.Lng, d.Lat, d.Lng) <= farthestAllowed,
            $"driver {d.DriverId} is not among the {limit} nearest"));
    }

    // Median time of a search, after a warm-up.
    public static async Task<double> MedianMsAsync(IDriverIndex index, IReadOnlyList<Search> searches, int limit = 10)
    {
        foreach (var s in searches.Take(3))
        {
            await index.NearbyAsync(s.Lat, s.Lng, s.RadiusM, limit, CancellationToken.None);
        }
        var times = new List<double>();
        foreach (var s in searches)
        {
            var clock = Stopwatch.StartNew();
            await index.NearbyAsync(s.Lat, s.Lng, s.RadiusM, limit, CancellationToken.None);
            times.Add(clock.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }
}
