using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Temporalio.Client;
using Testcontainers.PostgreSql;

namespace VideoPlatform.Tests;

[CollectionDefinition("infra")]
public sealed class InfraCollection : ICollectionFixture<InfraFixture>;

[CollectionDefinition("temporal")]
public sealed class TemporalCollection : ICollectionFixture<TemporalFixture>;

// One Postgres and one MinIO container for the whole run (Docker must be running). Every test gets
// its own cloned database and its own bucket.
public class InfraFixture : IAsyncLifetime
{
    // the image compose.yaml uses
    public const string MinioImage =
        "cgr.dev/chainguard/minio@sha256:f2e56aceb4d38ad9b05c901270946e2353b19f86ba4396f25cf75b15b455b38c";
    public const string AccessKey = "minio";
    public const string SecretKey = "minio-secret";

    private const string Template = "videos_template";
    private int _clones;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=500")
        .Build();

    private readonly IContainer _minio = new ContainerBuilder(MinioImage)
        .WithCommand("server", "/data")
        .WithEnvironment("MINIO_ROOT_USER", AccessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
        .WithPortBinding(9000, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/minio/health/live")))
        .Build();

    public string MinioUrl => $"http://{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";

    // For the tests' own look at MinIO.
    public IAmazonS3 S3 { get; private set; } = null!;

    public virtual async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _minio.StartAsync());
        S3 = new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey), new AmazonS3Config
        {
            ServiceURL = MinioUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
        await AdminAsync($"CREATE DATABASE {Template}");
        await using (var db = NpgsqlDataSource.Create(ConnectionString(Template)))
        {
            await Migrator.ApplyAsync(db);
        }
        // CREATE DATABASE ... TEMPLATE fails while anyone is connected to the template
        NpgsqlConnection.ClearAllPools();
    }

    public async Task<string> CloneAsync()
    {
        var name = $"videos_{Interlocked.Increment(ref _clones)}";
        await AdminAsync($"CREATE DATABASE {name} TEMPLATE {Template}");
        return ConnectionString(name);
    }

    public virtual async ValueTask DisposeAsync()
    {
        S3?.Dispose();
        await _postgres.DisposeAsync();
        await _minio.DisposeAsync();
    }

    // The keys in a bucket.
    public async Task<List<string>> KeysAsync(string bucket)
    {
        var response = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
        return (response.S3Objects ?? []).Select(o => o.Key).Order().ToList();
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

// Adds a Temporal dev server (stage 4).
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
