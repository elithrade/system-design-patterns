using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ScalingWrites.Tests;

[CollectionDefinition("infra")]
public sealed class InfraCollection : ICollectionFixture<InfraFixture>;

// One Postgres container for the whole run (Docker must be running).
// At start it builds two template databases: the main one (your migrations + a small seed) and an
// empty partition. Every test clones what it needs, so nothing leaks between tests.
public sealed class InfraFixture : IAsyncLifetime
{
    public const int Videos = 1_000;

    private const string MainTemplate = "videos_template";
    private const string PartitionTemplate = "partition_template";
    private int _clones;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=500")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        await AdminAsync($"CREATE DATABASE {MainTemplate}");
        await using (var db = NpgsqlDataSource.Create(ConnectionString(MainTemplate)))
        {
            await Migrator.ApplyAsync(db, Migrator.MainDirectory);
            await using var conn = await db.OpenConnectionAsync();
            var seed = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "seed.sql"));
            await new NpgsqlCommand(seed, conn).ExecuteNonQueryAsync();
            await new NpgsqlCommand($"CALL seed({Videos})", conn).ExecuteNonQueryAsync();
        }

        await AdminAsync($"CREATE DATABASE {PartitionTemplate}");
        await using (var db = NpgsqlDataSource.Create(ConnectionString(PartitionTemplate)))
        {
            await Migrator.ApplyAsync(db, Migrator.PartitionDirectory);
        }

        // CREATE DATABASE ... TEMPLATE fails while anyone is connected to the template
        NpgsqlConnection.ClearAllPools();
    }

    // A fresh copy of the seeded main database. Returns its connection string.
    public Task<string> CloneMainAsync() => CloneAsync(MainTemplate);

    // A fresh, empty partition database.
    public Task<string> ClonePartitionAsync() => CloneAsync(PartitionTemplate);

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    private async Task<string> CloneAsync(string template)
    {
        var name = $"db_{Interlocked.Increment(ref _clones)}";
        await AdminAsync($"CREATE DATABASE {name} TEMPLATE {template}");
        return ConnectionString(name);
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

public static class Sql
{
    public static async Task<T> ScalarAsync<T>(string db, string sql, object? args = null)
    {
        await using var conn = new NpgsqlConnection(db);
        return await conn.ExecuteScalarAsync<T>(sql, args) ?? default!;
    }

    public static async Task<long> ViewsColumnAsync(string db, long videoId) =>
        await ScalarAsync<long>(db, "SELECT views FROM videos WHERE id = @videoId", new { videoId });
}
