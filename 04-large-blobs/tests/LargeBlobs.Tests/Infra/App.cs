using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace LargeBlobs.Tests;

// The app under test, on its own cloned database and its own bucket, with a fake clock.
public sealed class App : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;

    // Starts at the real time: MinIO stamps objects with its own (real) clock, and the sweeper
    // compares those stamps with this clock.
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public string Bucket { get; }
    public string Db { get; }

    private App(Dictionary<string, string?> settings, string bucket, string db)
    {
        _settings = settings;
        Bucket = bucket;
        Db = db;
    }

    public static async Task<App> StartAsync(InfraFixture infra, Dictionary<string, string?>? extra = null)
    {
        var db = await infra.CloneAsync();
        var bucket = $"t{Guid.NewGuid():N}";
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Files"] = db,
            ["S3:ServiceUrl"] = infra.MinioUrl,
            ["S3:AccessKey"] = InfraFixture.AccessKey,
            ["S3:SecretKey"] = InfraFixture.SecretKey,
            ["Blobs:Bucket"] = bucket,
            ["Blobs:PartSizeMb"] = "5",
            ["Blobs:Sweeper"] = "false",
            ["Blobs:Notifications"] = "false",
            ["Blobs:WebhookToken"] = "",
            // the fixture migrated the template already
            ["Db:Migrate"] = "false",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }
        var app = new App(settings, bucket, db);
        // build the host now: it creates the bucket
        _ = app.Server;
        return app;
    }

    public Stats Stats => Services.GetRequiredService<Stats>();
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public HttpClient As(long userId)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(Endpoints.UserHeader, userId.ToString());
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // not Development: no developer exception page, so 500 failing requests stay cheap
        builder.UseEnvironment("Test");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time)));
    }
}

public static class Bytes
{
    public const int MiB = 1024 * 1024;

    // Deterministic, non-repeating-ish content, so a part in the wrong place shows up.
    public static byte[] Make(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}

public static class ClientExtensions
{
    // A client for presigned URLs: straight to MinIO, never through the app.
    public static readonly HttpClient Direct = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static async Task<UploadTicket> CreateFileAsync(this HttpClient client, string name, long size, string contentType = "application/octet-stream")
    {
        var response = await client.PostAsJsonAsync("/files", new NewFile(name, contentType, size));
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"POST /files: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<UploadTicket>())!;
    }

    public static async Task<HttpStatusCode> PutToUrlAsync(string url, byte[] bytes, string contentType = "application/octet-stream")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var response = await Direct.PutAsync(url, content);
        return response.StatusCode;
    }

    public static async Task<(HttpStatusCode Status, string Body)> PostAsync(this HttpClient client, string url)
    {
        using var response = await client.PostAsync(url, null);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public static async Task<FileRecord> FileAsync(this HttpClient client, long id) =>
        (await client.GetFromJsonAsync<FileRecord>($"/files/{id}"))!;
}
