using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace ScalingReads.Tests;

[CollectionDefinition("infra")]
public sealed class InfraCollection : ICollectionFixture<InfraFixture>;

// One Postgres and one Redis container for the whole run (Docker must be running).
// At start it builds a template database (your migrations + a small seed), and every test clones
// it, so each test starts from the same data and nothing leaks between tests.
public sealed class InfraFixture : IAsyncLifetime
{
    // Small enough to seed in a few seconds, big enough that the planner prefers an index.
    public const int Users = 5_000;
    public const int Posts = 200_000;
    public const int Celebrities = 3;
    public const int CelebrityLikes = 2_000;

    private const string Template = "feed_template";
    private int _clones;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=500")
        .Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    public string RedisConnectionString => _redis.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        await AdminAsync($"CREATE DATABASE {Template}");
        var template = ConnectionString(Template);
        await using (var db = NpgsqlDataSource.Create(template))
        {
            await Migrator.ApplyAsync(db);
            await using var conn = await db.OpenConnectionAsync();
            var seed = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "seed.sql"));
            await new NpgsqlCommand(seed, conn).ExecuteNonQueryAsync();
            await new NpgsqlCommand(
                $"CALL seed({Users}, {Posts}, 100000, {Celebrities}, {CelebrityLikes})", conn)
            { CommandTimeout = 300 }.ExecuteNonQueryAsync();
        }
        // CREATE DATABASE ... TEMPLATE fails while anyone is connected to the template
        NpgsqlConnection.ClearAllPools();
    }

    // A fresh copy of the seeded database. Returns its connection string.
    public async Task<string> CloneAsync()
    {
        var name = $"feed_{Interlocked.Increment(ref _clones)}";
        await AdminAsync($"CREATE DATABASE {name} TEMPLATE {Template}");
        return ConnectionString(name);
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    private string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database, MaxPoolSize = 30 }.ConnectionString;

    private async Task AdminAsync(string sql)
    {
        var admin = new NpgsqlConnectionStringBuilder(ConnectionString("postgres")) { Pooling = false };
        await using var conn = new NpgsqlConnection(admin.ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();
    }
}
