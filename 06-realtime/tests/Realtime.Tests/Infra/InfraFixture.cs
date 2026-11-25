using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Realtime.Tests;

[CollectionDefinition("infra")]
public sealed class InfraCollection : ICollectionFixture<InfraFixture>;

// One Postgres and one Redis container for the whole run (Docker must be running). The template
// database has 5 auctions at price 100; every test clones it and uses its own Redis channel prefix.
public sealed class InfraFixture : IAsyncLifetime
{
    private const string Template = "auctions_template";
    private int _clones;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=500")
        .Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    public string RedisConnectionString => _redis.GetConnectionString();

    // For the tests' own look at Redis.
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
            await new NpgsqlCommand("CALL seed(5)", conn).ExecuteNonQueryAsync();
        }
        // CREATE DATABASE ... TEMPLATE fails while anyone is connected to the template
        NpgsqlConnection.ClearAllPools();
    }

    public async Task<string> CloneAsync()
    {
        var name = $"auctions_{Interlocked.Increment(ref _clones)}";
        await AdminAsync($"CREATE DATABASE {name} TEMPLATE {Template}");
        return ConnectionString(name);
    }

    // How many connections are subscribed to a channel, as Redis sees it.
    public async Task<long> SubscribersAsync(string channel)
    {
        var result = await Redis.GetServers()[0].ExecuteAsync("PUBSUB", "NUMSUB", channel);
        return (long)((RedisResult[])result!)[1];
    }

    public async ValueTask DisposeAsync()
    {
        Redis?.Dispose();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    private string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database, MaxPoolSize = 40 }.ConnectionString;

    private async Task AdminAsync(string sql)
    {
        var admin = new NpgsqlConnectionStringBuilder(ConnectionString("postgres")) { Pooling = false };
        await using var conn = new NpgsqlConnection(admin.ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();
    }
}
