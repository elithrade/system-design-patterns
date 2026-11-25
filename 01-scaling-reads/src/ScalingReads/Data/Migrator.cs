using Npgsql;

namespace ScalingReads;

// Applies db/migrations/*.sql that are not yet in schema_migrations, in name order, each in its own
// transaction. Runs at startup against the primary; the replica gets the changes through replication.
public static class Migrator
{
    public static string MigrationsDirectory => Path.Combine(AppContext.BaseDirectory, "db", "migrations");

    // upTo: apply only files whose name sorts at or before it (the tests use this to seed between
    // the baseline and your migrations). Null applies everything.
    public static async Task ApplyAsync(NpgsqlDataSource db, ILogger? logger = null, CancellationToken ct = default, string? upTo = null)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await Exec(conn, null, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                name       text PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT now())
            """, ct);

        foreach (var file in Directory.GetFiles(MigrationsDirectory, "*.sql").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (upTo is not null && StringComparer.Ordinal.Compare(name, upTo) > 0)
            {
                break;
            }
            await using var tx = await conn.BeginTransactionAsync(ct);
            // one migrator at a time, if several instances start together
            await Exec(conn, tx, "SELECT pg_advisory_xact_lock(4242)", ct);

            await using var check = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE name = @name", conn, tx);
            check.Parameters.AddWithValue("name", name);
            if (await check.ExecuteScalarAsync(ct) is not null)
            {
                continue;
            }

            logger?.LogInformation("Applying migration {Name}", name);
            await Exec(conn, tx, await File.ReadAllTextAsync(file, ct), ct);
            await using var record = new NpgsqlCommand("INSERT INTO schema_migrations (name) VALUES (@name)", conn, tx);
            record.Parameters.AddWithValue("name", name);
            await record.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
