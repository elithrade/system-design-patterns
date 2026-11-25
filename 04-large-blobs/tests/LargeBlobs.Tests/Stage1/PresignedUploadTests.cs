using System.Net;

namespace LargeBlobs.Tests.Stage1;

// Stage 1 goal: the bytes go from the client to MinIO; the API only signs and checks.
[Collection("infra")]
public sealed class PresignedUploadTests(InfraFixture infra)
{
    [Fact]
    public async Task Create_put_complete_and_the_file_is_ready()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var bytes = Bytes.Make(3 * Bytes.MiB);

        var ticket = await client.CreateFileAsync("video.mp4", bytes.Length, "video/mp4");
        Assert.Equal("pending", (await client.FileAsync(ticket.Id)).Status);
        Assert.Equal(HttpStatusCode.OK, await ClientExtensions.PutToUrlAsync(ticket.UploadUrl, bytes, "video/mp4"));
        var (status, body) = await client.PostAsync($"/files/{ticket.Id}/complete");

        Assert.True(status == HttpStatusCode.OK, $"complete: {(int)status} {body}");
        var file = await client.FileAsync(ticket.Id);
        Assert.Equal("ready", file.Status);
        Assert.Equal(bytes, await infra.ReadObjectAsync(app.Bucket, Blobs.KeyFor(ticket.Id)));
        Assert.Equal(0, app.Stats.BytesThroughApi);
    }

    [Fact]
    public async Task The_url_expires_when_it_says()
    {
        await using var app = await App.StartAsync(infra, new() { ["Blobs:UploadUrlSeconds"] = "1" });
        var client = app.As(1);

        var ticket = await client.CreateFileAsync("late.bin", 10);
        Assert.Equal(app.Time.GetUtcNow().AddSeconds(1), ticket.ExpiresAt);
        // MinIO checks the signature against its own clock
        await Task.Delay(2500);

        Assert.Equal(HttpStatusCode.Forbidden, await ClientExtensions.PutToUrlAsync(ticket.UploadUrl, new byte[10]));
    }

    [Fact]
    public async Task The_url_only_takes_the_declared_content_type()
    {
        await using var app = await App.StartAsync(infra);
        var ticket = await app.As(1).CreateFileAsync("doc.pdf", 10, "application/pdf");

        Assert.Equal(HttpStatusCode.Forbidden, await ClientExtensions.PutToUrlAsync(ticket.UploadUrl, new byte[10], "text/html"));
        Assert.Equal(HttpStatusCode.OK, await ClientExtensions.PutToUrlAsync(ticket.UploadUrl, new byte[10], "application/pdf"));
    }

    [Fact]
    public async Task Completing_before_uploading_is_missing()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var ticket = await client.CreateFileAsync("never.bin", 10);

        var (status, body) = await client.PostAsync($"/files/{ticket.Id}/complete");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("Missing", body);
        Assert.Equal("pending", (await client.FileAsync(ticket.Id)).Status);
    }

    [Fact]
    public async Task The_wrong_size_is_a_mismatch_and_the_file_stays_pending()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var ticket = await client.CreateFileAsync("short.bin", 100);
        await ClientExtensions.PutToUrlAsync(ticket.UploadUrl, new byte[60]);

        var (status, body) = await client.PostAsync($"/files/{ticket.Id}/complete");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("SizeMismatch", body);
        Assert.Equal("pending", (await client.FileAsync(ticket.Id)).Status);
    }

    [Fact]
    public async Task Completing_twice_is_not_pending_and_a_missing_file_is_not_found()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var ticket = await client.CreateFileAsync("twice.bin", 10);
        await ClientExtensions.PutToUrlAsync(ticket.UploadUrl, new byte[10]);
        await client.PostAsync($"/files/{ticket.Id}/complete");

        var (again, body) = await client.PostAsync($"/files/{ticket.Id}/complete");

        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Contains("NotPending", body);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/files/999999/complete")).Status);
    }
}
