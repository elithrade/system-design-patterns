using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Temporalio.Client;
using Testcontainers.PostgreSql;

namespace MultiStep.Tests;

[CollectionDefinition("infra")]
public sealed class InfraCollection : ICollectionFixture<InfraFixture>;

[CollectionDefinition("temporal")]
public sealed class TemporalCollection : ICollectionFixture<TemporalFixture>;

// One Postgres container (Docker must be running). The template database is seeded with seed(1000):
// mug and poster with 1,000 units, scarce with 20. Every test clones it.
public class InfraFixture : IAsyncLifetime
{
    private const string Template = "shop_template";
    private int _clones;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=500")
        .Build();

    public virtual async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await AdminAsync($"CREATE DATABASE {Template}");
        await using (var db = NpgsqlDataSource.Create(ConnectionString(Template)))
        {
            await Migrator.ApplyAsync(db);
            await using var conn = await db.OpenConnectionAsync();
            await new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "seed.sql")), conn).ExecuteNonQueryAsync();
            await new NpgsqlCommand("CALL seed(1000)", conn).ExecuteNonQueryAsync();
        }
        // CREATE DATABASE ... TEMPLATE fails while anyone is connected to the template
        NpgsqlConnection.ClearAllPools();
    }

    public async Task<string> CloneAsync()
    {
        var name = $"shop_{Interlocked.Increment(ref _clones)}";
        await AdminAsync($"CREATE DATABASE {name} TEMPLATE {Template}");
        return ConnectionString(name);
    }

    public virtual async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

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

// Postgres plus a Temporal dev server (the image compose.yaml uses), for stage 4.
public sealed class TemporalFixture : InfraFixture
{
    public const string Image = "temporalio/temporal:1.10.0";

    private readonly IContainer _temporal = new ContainerBuilder(Image)
        .WithCommand("server", "start-dev", "--ip", "0.0.0.0", "--headless")
        .WithPortBinding(7233, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("temporal", "operator", "cluster", "health", "--address", "localhost:7233"))
        .Build();

    public string Address => $"{_temporal.Hostname}:{_temporal.GetMappedPublicPort(7233)}";

    // For the tests' own look at workflows.
    public ITemporalClient Client { get; private set; } = null!;

    public override async ValueTask InitializeAsync()
    {
        await Task.WhenAll(base.InitializeAsync().AsTask(), _temporal.StartAsync());
        Client = await TemporalClient.ConnectAsync(new(Address));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _temporal.DisposeAsync();
    }
}
