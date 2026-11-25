using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Proximity.Tests;

[CollectionDefinition("infra")]
public sealed class InfraCollection : ICollectionFixture<InfraFixture>;

// One PostGIS and one Redis container for the whole run (Docker must be running). The template
// database has your migrations and seed(100000); every test clones it.
public sealed class InfraFixture : IAsyncLifetime
{
    public const int Drivers = 100_000;
    private const string Template = "rides_template";
    private int _clones;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgis/postgis:17-3.6")
        // as in compose.yaml: no JIT
        .WithCommand("-c", "max_connections=500", "-c", "jit=off")
        .Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    public string RedisConnectionString => _redis.GetConnectionString();
    public IConnectionMultiplexer Redis { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        Redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
        await AdminAsync($"CREATE DATABASE {Template}");
        await using (var db = NpgsqlDataSource.Create(ConnectionString(Template)))
        {
            await Migrator.ApplyAsync(db);
            await using var conn = await db.OpenConnectionAsync();
            await new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "seed.sql")), conn).ExecuteNonQueryAsync();
            await new NpgsqlCommand($"CALL seed({Drivers})", conn) { CommandTimeout = 300 }.ExecuteNonQueryAsync();
        }
        // CREATE DATABASE ... TEMPLATE fails while anyone is connected to the template
        NpgsqlConnection.ClearAllPools();
    }

    public async Task<string> CloneAsync()
    {
        var name = $"rides_{Interlocked.Increment(ref _clones)}";
        await AdminAsync($"CREATE DATABASE {name} TEMPLATE {Template}");
        return ConnectionString(name);
    }

    public async ValueTask DisposeAsync()
    {
        Redis?.Dispose();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    private string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database, MaxPoolSize = 40, CommandTimeout = 120 }.ConnectionString;

    private async Task AdminAsync(string sql)
    {
        var admin = new NpgsqlConnectionStringBuilder(ConnectionString("postgres")) { Pooling = false };
        await using var conn = new NpgsqlConnection(admin.ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();
    }
}
