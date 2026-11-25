using System.Net;

namespace LargeBlobs.Tests.Stage5;

// Stage 5 goal: downloads go straight from MinIO, only to the owner, for a short time.
// Ready files are made directly (a row and an object), so these tests do not depend on stages 1-4.
[Collection("infra")]
public sealed class DownloadTests(InfraFixture infra)
{
    private async Task<long> ReadyFileAsync(App app, long owner, string name, byte[] bytes)
    {
        var id = await app.Get<FileRepository>().InsertAsync(owner, name, "application/pdf", bytes.Length, "ready", CancellationToken.None);
        await infra.PutObjectAsync(app.Bucket, Blobs.KeyFor(id), bytes);
        return id;
    }

    [Fact]
    public async Task The_owner_is_redirected_to_the_bytes()
    {
        await using var app = await App.StartAsync(infra);
        var bytes = Bytes.Make(2 * Bytes.MiB);
        var id = await ReadyFileAsync(app, owner: 1, "report.pdf", bytes);

        var response = await app.As(1).GetAsync($"/files/{id}/download");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var download = await ClientExtensions.Direct.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        var disposition = download.Content.Headers.ContentDisposition;
        Assert.Equal("attachment", disposition?.DispositionType);
        Assert.Equal("report.pdf", disposition?.FileName?.Trim('"'));
        Assert.Equal(0, app.Stats.BytesThroughApi);
    }

    [Fact]
    public async Task Someone_else_is_forbidden()
    {
        await using var app = await App.StartAsync(infra);
        var id = await ReadyFileAsync(app, owner: 1, "private.pdf", new byte[10]);

        var response = await app.As(2).GetAsync($"/files/{id}/download");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Pending_and_missing_files()
    {
        await using var app = await App.StartAsync(infra);
        var pending = await app.Get<FileRepository>().InsertAsync(1, "wip.pdf", "application/pdf", 10, "pending", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, (await app.As(1).GetAsync($"/files/{pending}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.As(1).GetAsync("/files/999999/download")).StatusCode);
    }

    [Fact]
    public async Task The_link_expires()
    {
        await using var app = await App.StartAsync(infra, new() { ["Blobs:DownloadSeconds"] = "1" });
        var id = await ReadyFileAsync(app, owner: 1, "brief.pdf", new byte[10]);
        var response = await app.As(1).GetAsync($"/files/{id}/download");

        // MinIO checks the signature against its own clock
        await Task.Delay(2500);
        using var download = await ClientExtensions.Direct.GetAsync(response.Headers.Location);

        Assert.Equal(HttpStatusCode.Forbidden, download.StatusCode);
    }
}
