using System.Net;

namespace VideoPlatform.Tests.Baseline;

// Guards: the baseline works for one small video. These pass from the start.
[Collection("infra")]
public sealed class DirectUploadTests(InfraFixture infra)
{
    private static async Task<(HttpResponseMessage Response, long Id)> UploadAsync(App app, byte[] content)
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new("video/mp4");
        var response = await app.Client().PutAsync("/videos/direct?title=clip", body);
        var id = long.Parse(response.Headers.Location!.ToString().Split('/')[^1]);
        return (response, id);
    }

    [Fact]
    public async Task A_direct_upload_is_published_with_every_rendition()
    {
        await using var app = await App.StartAsync(infra);

        var (response, id) = await UploadAsync(app, App.Source(App.Mb));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var video = await app.SnapshotAsync(id);
        Assert.Equal(VideoStatus.Published, video.Status);
        Assert.All(video.Renditions, r => Assert.Equal(("done", 100), (r.Status, r.Percent)));
        Assert.Equal([Blobs.RenditionKey(id, "360p"), Blobs.RenditionKey(id, "720p"), Blobs.SourceKey(id), Blobs.RenditionKey(id, "thumb")],
            await infra.KeysAsync(app.Bucket));
        Assert.Equal(App.Mb, app.Stats.BytesThroughApiCount);
    }

    [Fact]
    public async Task A_corrupt_video_fails()
    {
        await using var app = await App.StartAsync(infra);

        var (_, id) = await UploadAsync(app, App.Source(App.Mb, corrupt: true));

        var video = await app.SnapshotAsync(id);
        Assert.Equal(VideoStatus.Failed, video.Status);
        Assert.Contains("not a video", video.Reason);
    }

    [Fact]
    public async Task A_published_rendition_redirects_to_minio()
    {
        await using var app = await App.StartAsync(infra);
        var (_, id) = await UploadAsync(app, App.Source(App.Mb));
        var client = app.Server.CreateClient();

        var response = await client.GetAsync($"/videos/{id}/renditions/720p");
        var missing = await client.GetAsync($"/videos/{id}/renditions/4k");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("720p rendition", await Http.Direct.GetStringAsync(response.Headers.Location));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
