using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;

namespace LargeBlobs.Tests;

[CollectionDefinition("infra")]
public sealed class InfraCollection : ICollectionFixture<InfraFixture>;

// One Postgres and one MinIO container for the whole run (Docker must be running).
// Every test gets its own cloned database and its own bucket.
public sealed class InfraFixture : IAsyncLifetime
{
    // the image compose.yaml uses
    public const string MinioImage =
        "cgr.dev/chainguard/minio@sha256:f2e56aceb4d38ad9b05c901270946e2353b19f86ba4396f25cf75b15b455b38c";
    public const string AccessKey = "minio";
    public const string SecretKey = "minio-secret";

    private const string Template = "files_template";
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

    // For the tests' own checks of what is in MinIO.
    public IAmazonS3 S3 { get; private set; } = null!;

    public async ValueTask InitializeAsync()
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
        var name = $"files_{Interlocked.Increment(ref _clones)}";
        await AdminAsync($"CREATE DATABASE {name} TEMPLATE {Template}");
        return ConnectionString(name);
    }

    public async ValueTask DisposeAsync()
    {
        S3?.Dispose();
        await _postgres.DisposeAsync();
        await _minio.DisposeAsync();
    }

    // Every key in a bucket.
    public async Task<List<S3Object>> ObjectsAsync(string bucket)
    {
        var response = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
        return response.S3Objects ?? [];
    }

    // Multipart uploads still open in a bucket.
    public async Task<List<MultipartUpload>> OpenUploadsAsync(string bucket)
    {
        var response = await S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket });
        return response.MultipartUploads ?? [];
    }

    public async Task<byte[]> ReadObjectAsync(string bucket, string key)
    {
        using var response = await S3.GetObjectAsync(bucket, key);
        var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    public Task PutObjectAsync(string bucket, string key, byte[] bytes) =>
        S3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(bytes) });

    private string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database, MaxPoolSize = 20 }.ConnectionString;

    private async Task AdminAsync(string sql)
    {
        var admin = new NpgsqlConnectionStringBuilder(ConnectionString("postgres")) { Pooling = false };
        await using var conn = new NpgsqlConnection(admin.ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();
    }
}
