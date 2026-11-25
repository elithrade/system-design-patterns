using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Contention.Tests;

[CollectionDefinition("infra")]
public sealed class InfraCollection : ICollectionFixture<InfraFixture>;

// One Postgres and one Redis container for the whole run (Docker must be running).
// At start it builds two template databases: sales (your migrations + sale 1 with 100 tickets and
// 100 seats, limit 2 per buyer) and an empty payments database. Every test clones what it needs.
public sealed class InfraFixture : IAsyncLifetime
{
    public const int Capacity = 100;
    public const int PerBuyerLimit = 2;

    private const string SalesTemplate = "sales_template";
    private const string PaymentsTemplate = "payments_template";
    private int _clones;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=500")
        .Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    public string RedisConnectionString => _redis.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        await AdminAsync($"CREATE DATABASE {SalesTemplate}");
        await using (var db = NpgsqlDataSource.Create(ConnectionString(SalesTemplate)))
        {
            await Migrator.ApplyAsync(db, Migrator.SalesDirectory);
            await using var conn = await db.OpenConnectionAsync();
            var seed = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "seed.sql"));
            await new NpgsqlCommand(seed, conn).ExecuteNonQueryAsync();
            await new NpgsqlCommand($"CALL seed({Capacity}, {PerBuyerLimit})", conn).ExecuteNonQueryAsync();
        }

        await AdminAsync($"CREATE DATABASE {PaymentsTemplate}");
        await using (var db = NpgsqlDataSource.Create(ConnectionString(PaymentsTemplate)))
        {
            await Migrator.ApplyAsync(db, Migrator.PaymentsDirectory);
        }

        // CREATE DATABASE ... TEMPLATE fails while anyone is connected to the template
        NpgsqlConnection.ClearAllPools();
    }

    public Task<string> CloneSalesAsync() => CloneAsync(SalesTemplate);
    public Task<string> ClonePaymentsAsync() => CloneAsync(PaymentsTemplate);

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    private async Task<string> CloneAsync(string template)
    {
        var name = $"db_{Interlocked.Increment(ref _clones)}";
        await AdminAsync($"CREATE DATABASE {name} TEMPLATE {template}");
        return ConnectionString(name);
    }

    private string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database, MaxPoolSize = 50 }.ConnectionString;

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

    public static async Task ExecAsync(string db, string sql, object? args = null)
    {
        await using var conn = new NpgsqlConnection(db);
        await conn.ExecuteAsync(sql, args);
    }
}
