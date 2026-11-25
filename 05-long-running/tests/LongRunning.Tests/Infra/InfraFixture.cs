using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;

namespace LongRunning.Tests;

[CollectionDefinition("infra")]
public sealed class InfraCollection : ICollectionFixture<InfraFixture>;

// One Postgres container for the whole run (Docker must be running). At start it builds a template
// database (your migrations + 50 users with 20 activities each), and every test clones it.
public sealed class InfraFixture : IAsyncLifetime
{
    private const string Template = "stride_template";
    private int _clones;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=500")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await AdminAsync($"CREATE DATABASE {Template}");
        await using (var db = NpgsqlDataSource.Create(ConnectionString(Template)))
        {
            await Migrator.ApplyAsync(db);
            await using var conn = await db.OpenConnectionAsync();
            await new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "seed.sql")), conn).ExecuteNonQueryAsync();
            await new NpgsqlCommand("CALL seed(50, 20)", conn).ExecuteNonQueryAsync();
        }
        // CREATE DATABASE ... TEMPLATE fails while anyone is connected to the template
        NpgsqlConnection.ClearAllPools();
    }

    public async Task<string> CloneAsync()
    {
        var name = $"stride_{Interlocked.Increment(ref _clones)}";
        await AdminAsync($"CREATE DATABASE {name} TEMPLATE {Template}");
        return ConnectionString(name);
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

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

// Direct access to the jobs table, so stage 2-4 tests do not depend on your JobStore.
public sealed record JobRow(long Id, string Status, int Attempts, string? LockedBy, DateTime? LeaseUntil, DateTime RunAfter, string? LastError, bool HasResult);

public static class JobsTable
{
    // every inserted job gets its own year, so none collides with stage 1's one-active-job rule
    private static int _year = 3000;

    public static async Task<long> InsertAsync(string db, DateTimeOffset now, long userId = 1, string kind = JobKinds.YearInReview, DateTimeOffset? runAfter = null)
    {
        await using var conn = new NpgsqlConnection(db);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO jobs (kind, user_id, year, status, run_after, created_at)
            VALUES (@kind, @userId, @year, 'queued', @runAfter, @now) RETURNING id
            """, new { kind, userId, year = Interlocked.Increment(ref _year), runAfter = runAfter ?? now, now });
    }

    public static async Task<JobRow> GetAsync(string db, long id)
    {
        await using var conn = new NpgsqlConnection(db);
        return await conn.QuerySingleAsync<JobRow>(
            "SELECT id, status, attempts, locked_by, lease_until, run_after, last_error, result IS NOT NULL AS has_result FROM jobs WHERE id = @id",
            new { id });
    }
}
