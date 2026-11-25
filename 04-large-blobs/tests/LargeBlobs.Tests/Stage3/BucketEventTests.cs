using System.Net;
using System.Net.Http.Json;

namespace LargeBlobs.Tests.Stage3;

// Stage 3 goal: MinIO's event, not the client's promise, marks a file ready.
// MinIO cannot reach the in-memory test server, so these tests POST the events MinIO would send.
[Collection("infra")]
public sealed class BucketEventTests(InfraFixture infra)
{
    private static object Event(string bucket, long fileId, long size, string eventName = "s3:ObjectCreated:Put") =>
        Events(bucket, (fileId, size, eventName));

    private static object Events(string bucket, params (long FileId, long Size, string EventName)[] records) => new
    {
        EventName = records[0].EventName,
        Key = $"{bucket}/files/{records[0].FileId}",
        Records = records.Select(r => new
        {
            eventVersion = "2.0",
            eventSource = "minio:s3",
            eventName = r.EventName,
            s3 = new
            {
                bucket = new { name = bucket },
                @object = new { key = $"files%2F{r.FileId}", size = r.Size, eTag = "0123456789abcdef" },
            },
        }).ToArray(),
    };

    private static async Task<int> SendAsync(HttpClient client, object e)
    {
        var response = await client.PostAsJsonAsync("/events/minio", e);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"event: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<EventAnswer>())!.Changed;
    }

    private sealed record EventAnswer(int Changed);

    [Fact]
    public async Task The_event_alone_makes_an_uploaded_file_ready()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var bytes = Bytes.Make(Bytes.MiB);
        var ticket = await client.CreateFileAsync("clip.mp4", bytes.Length);
        await ClientExtensions.PutToUrlAsync(ticket.UploadUrl, bytes);

        Assert.Equal(1, await SendAsync(client, Event(app.Bucket, ticket.Id, bytes.Length)));

        var file = await client.FileAsync(ticket.Id);
        Assert.Equal("ready", file.Status);
        Assert.NotNull(file.ReadyAt);
    }

    [Fact]
    public async Task A_repeated_event_changes_nothing()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var ticket = await client.CreateFileAsync("twice.bin", 10);
        await SendAsync(client, Event(app.Bucket, ticket.Id, 10));
        var first = await client.FileAsync(ticket.Id);
        app.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, await SendAsync(client, Event(app.Bucket, ticket.Id, 10)));

        Assert.Equal(first, await client.FileAsync(ticket.Id));
    }

    [Fact]
    public async Task A_size_mismatch_fails_the_file()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var ticket = await client.CreateFileAsync("liar.bin", 100);

        Assert.Equal(1, await SendAsync(client, Event(app.Bucket, ticket.Id, 99)));

        Assert.Equal("failed", (await client.FileAsync(ticket.Id)).Status);
    }

    [Fact]
    public async Task Several_records_in_one_event_are_all_handled()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var a = await client.CreateFileAsync("a.bin", 10);
        var b = await client.CreateFileAsync("b.bin", 20);

        var changed = await SendAsync(client, Events(app.Bucket,
            (a.Id, 10, "s3:ObjectCreated:Put"), (b.Id, 20, "s3:ObjectCreated:CompleteMultipartUpload")));

        Assert.Equal(2, changed);
        Assert.Equal("ready", (await client.FileAsync(a.Id)).Status);
        Assert.Equal("ready", (await client.FileAsync(b.Id)).Status);
    }

    [Fact]
    public async Task Other_events_buckets_and_keys_change_nothing()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var ticket = await client.CreateFileAsync("left.bin", 10);

        Assert.Equal(0, await SendAsync(client, Event(app.Bucket, ticket.Id, 10, "s3:ObjectRemoved:Delete")));
        Assert.Equal(0, await SendAsync(client, Event("someone-elses-bucket", ticket.Id, 10)));
        Assert.Equal(0, await SendAsync(client, Event(app.Bucket, 999_999, 10)));
        var notOurs = new
        {
            Records = new[]
            {
                new { eventName = "s3:ObjectCreated:Put", s3 = new { bucket = new { name = app.Bucket }, @object = new { key = "avatars%2F7.png", size = 10L } } },
            },
        };
        Assert.Equal(0, await SendAsync(client, notOurs));

        Assert.Equal("pending", (await client.FileAsync(ticket.Id)).Status);
    }

    [Fact]
    public async Task Events_without_the_token_are_refused()
    {
        await using var app = await App.StartAsync(infra, new() { ["Blobs:WebhookToken"] = "secret" });
        var client = app.As(1);
        var ticket = await client.CreateFileAsync("spoof.bin", 10);

        var response = await client.PostAsJsonAsync("/events/minio", Event(app.Bucket, ticket.Id, 10));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("pending", (await client.FileAsync(ticket.Id)).Status);
    }
}
