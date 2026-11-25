using System.Net.Http.Json;
using Amazon.S3.Model;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace VideoPlatform.Tests;

// The app under test, with its own database, bucket and fake clock. Two Apps can share a database
// and bucket to play two instances (stage 3). Transcoding is fast: a 1 MB source takes
// MsPerMb x cost ms per rendition (360p 1, 720p 2, thumb 0.1).
public sealed class App : WebApplicationFactory<Program>
{
    public const int LeaseSeconds = 30;
    public const int BackoffSeconds = 2;
    public const int MaxAttempts = 3;
    public const int HeartbeatSeconds = 15;
    public const int Mb = 1024 * 1024;
    public static readonly string[] Renditions = ["360p", "720p", "thumb"];

    static App() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private readonly Dictionary<string, string?> _settings;

    // real time, so presigned URLs MinIO checks against its own clock work
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    public string Db { get; }
    public string Bucket { get; }

    private App(Dictionary<string, string?> settings, string db, string bucket)
    {
        _settings = settings;
        Db = db;
        Bucket = bucket;
    }

    public static async Task<App> StartAsync(InfraFixture infra, Engine engine = Engine.Inline, BusKind bus = BusKind.Local,
        App? sameBackendAs = null, Dictionary<string, string?>? extra = null)
    {
        var db = sameBackendAs?.Db ?? await infra.CloneAsync();
        var bucket = sameBackendAs?.Bucket ?? $"t{Guid.NewGuid():N}";
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Videos"] = db,
            ["S3:ServiceUrl"] = infra.MinioUrl,
            ["S3:AccessKey"] = InfraFixture.AccessKey,
            ["S3:SecretKey"] = InfraFixture.SecretKey,
            ["Storage:Bucket"] = bucket,
            ["Storage:UploadUrlSeconds"] = "900",
            ["Storage:MaxUploadMb"] = "100",
            ["Pipeline:Engine"] = engine.ToString(),
            ["Pipeline:Bus"] = bus.ToString(),
            ["Transcode:MsPerMb"] = "50",
            ["Transcode:RunWorkers"] = "false",
            ["Transcode:LeaseSeconds"] = LeaseSeconds.ToString(),
            ["Transcode:BackoffSeconds"] = BackoffSeconds.ToString(),
            ["Transcode:MaxAttempts"] = MaxAttempts.ToString(),
            ["Live:HeartbeatSeconds"] = HeartbeatSeconds.ToString(),
            // the fixture migrated the template already
            ["Db:Migrate"] = "false",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        if (infra is TemporalFixture temporal)
        {
            settings["Temporal:Address"] = temporal.Address;
            var queue = $"q{Guid.NewGuid():N}";
            settings["Temporal:TaskQueue"] = queue;
            settings["Temporal:WorkflowIdPrefix"] = $"{queue}-";
        }
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }
        var app = new App(settings, db, bucket);
        _ = app.Server;
        return app;
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
    public VideoStore Videos => Get<VideoStore>();
    public Stats Stats => Get<Stats>();
    public Faults Faults => Get<Faults>();

    // A video whose source is in MinIO and which is marked uploaded, without your stage 1 code.
    // With the Temporal engine, its workflow is started first, as POST /videos would.
    public async Task<long> UploadedVideoAsync(int bytes = Mb, bool corrupt = false)
    {
        var video = await Videos.CreateAsync(1, "clip", "video/mp4", bytes, CancellationToken.None);
        await Get<IPipeline>().OnCreatedAsync(video.Id, CancellationToken.None);
        await PutSourceAsync(video.Id, bytes, corrupt);
        Assert.True(await Videos.SetStatusAsync(video.Id, [VideoStatus.Pending], VideoStatus.Uploaded, null, CancellationToken.None));
        return video.Id;
    }

    public async Task PutSourceAsync(long videoId, int bytes = Mb, bool corrupt = false)
    {
        var content = Source(bytes, corrupt);
        await Get<Blobs>().S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = Blobs.SourceKey(videoId),
            InputStream = new MemoryStream(content),
            ContentType = "video/mp4",
        });
    }

    // Bytes for a fake video. A corrupt one starts with "CORRUPT".
    public static byte[] Source(int bytes, bool corrupt = false)
    {
        var content = new byte[bytes];
        for (var i = 0; i < bytes; i++)
        {
            content[i] = (byte)(i % 251);
        }
        if (corrupt)
        {
            "CORRUPT"u8.CopyTo(content);
        }
        return content;
    }

    public async Task<VideoSnapshot> SnapshotAsync(long videoId) =>
        await Videos.SnapshotAsync(videoId, CancellationToken.None) ?? throw new InvalidOperationException($"no video {videoId}");

    // Changes a video the way the pipelines do, then publishes its new state.
    public async Task ChangeAsync(long videoId, Func<VideoStore, Task> change)
    {
        await change(Videos);
        await Get<Progress>().PublishAsync(videoId, CancellationToken.None);
    }

    public async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var conn = new NpgsqlConnection(Db);
        return await conn.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("no value");
    }

    public async Task<VideoSnapshot> WaitForFinalAsync(long videoId, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var snapshot = await SnapshotAsync(videoId);
            if (snapshot.IsFinal)
            {
                return snapshot;
            }
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"video {videoId} still {snapshot.Status} after {timeoutMs} ms: {string.Join(", ", snapshot.Renditions)}");
            }
            await Task.Delay(100);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // not Development: no developer exception page
        builder.UseEnvironment("Test");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time)));
    }
}

public static class Http
{
    // For presigned URLs, which point at MinIO, not the test server.
    public static readonly HttpClient Direct = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static HttpClient Client(this App app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "1");
        return client;
    }

    public static async Task<UploadTicket> CreateAsync(this HttpClient client, long size, string contentType = "video/mp4")
    {
        var response = await client.PostAsJsonAsync("/videos", new NewVideo("clip", contentType, size));
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UploadTicket>(Json.Options))!;
    }

    public static async Task<HttpResponseMessage> PutAsync(UploadTicket ticket, byte[] content, string contentType = "video/mp4")
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new(contentType);
        return await Direct.PutAsync(ticket.UploadUrl, body);
    }

    public static Task<HttpResponseMessage> ConfirmAsync(this HttpClient client, long videoId) =>
        client.PostAsync($"/videos/{videoId}/uploaded", null);
}
