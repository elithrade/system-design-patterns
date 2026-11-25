using System.Net;
using System.Net.Http.Json;

namespace VideoPlatform.Tests.Stage1;

// Stage 1 goal: the API signs, MinIO takes the bytes, and confirming starts processing once.
// These run the inline engine: confirming a video makes its renditions before it answers.
[Collection("infra")]
public sealed class PresignedUploadTests(InfraFixture infra)
{
    [Fact]
    public async Task Upload_to_the_url_then_confirm_publishes_the_video()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.Client();
        var ticket = await client.CreateAsync(App.Mb);

        var put = await Http.PutAsync(ticket, App.Source(App.Mb));
        var confirm = await client.ConfirmAsync(ticket.Id);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);
        Assert.Equal(VideoStatus.Published, (await app.SnapshotAsync(ticket.Id)).Status);
        Assert.Equal(0L, app.Stats.BytesThroughApiCount);
        Assert.True(ticket.ExpiresAt > app.Time.GetUtcNow().AddSeconds(800));
    }

    [Theory]
    [InlineData("image/png", 1000)]
    [InlineData("video/mp4", 0)]
    [InlineData("video/mp4", 101L * App.Mb)]
    public async Task Not_a_video_or_the_wrong_size_is_refused(string contentType, long size)
    {
        await using var app = await App.StartAsync(infra);

        var response = await app.Client().PostAsJsonAsync("/videos", new NewVideo("clip", contentType, size));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await app.ScalarAsync<long>("SELECT count(*) FROM videos"));
    }

    [Fact]
    public async Task The_url_refuses_another_content_type()
    {
        await using var app = await App.StartAsync(infra);
        var ticket = await app.Client().CreateAsync(App.Mb);

        var put = await Http.PutAsync(ticket, App.Source(App.Mb), contentType: "text/html");

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task Confirming_before_the_upload_or_with_the_wrong_size_leaves_it_pending()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.Client();
        var ticket = await client.CreateAsync(App.Mb);

        var early = await client.ConfirmAsync(ticket.Id);
        await Http.PutAsync(ticket, App.Source(App.Mb / 2));
        var wrongSize = await client.ConfirmAsync(ticket.Id);

        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("NotUploaded", await early.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, wrongSize.StatusCode);
        Assert.Contains("SizeMismatch", await wrongSize.Content.ReadAsStringAsync());
        Assert.Equal(VideoStatus.Pending, (await app.SnapshotAsync(ticket.Id)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await client.ConfirmAsync(999)).StatusCode);
    }

    [Fact]
    public async Task Confirming_again_makes_nothing_twice()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.Client();
        var ticket = await client.CreateAsync(App.Mb);
        await Http.PutAsync(ticket, App.Source(App.Mb));
        await client.ConfirmAsync(ticket.Id);

        var again = await client.ConfirmAsync(ticket.Id);

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(App.Renditions.Length, app.Stats.Transcodes);
        Assert.Equal(1, app.Stats.PublishedCount);
    }

    [Fact]
    public async Task Of_two_confirms_at_once_exactly_one_confirms()
    {
        await using var app = await App.StartAsync(infra);
        var uploads = app.Get<VideoUploads>();
        var ids = new List<long>();
        for (var i = 0; i < 10; i++)
        {
            var ticket = await app.Client().CreateAsync(1000);
            await Http.PutAsync(ticket, App.Source(1000));
            ids.Add(ticket.Id);
        }

        foreach (var id in ids)
        {
            var results = await Concurrently.RunAsync(2, _ => uploads.ConfirmAsync(id, CancellationToken.None));
            Assert.Equal([ConfirmResult.Confirmed, ConfirmResult.AlreadyConfirmed], results.Order());
        }
    }
}
