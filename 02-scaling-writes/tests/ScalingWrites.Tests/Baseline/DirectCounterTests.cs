using System.Net;

namespace ScalingWrites.Tests.Baseline;

// Guards: the baseline counter is correct, just slow under load. These pass from the start.
[Collection("infra")]
public sealed class DirectCounterTests(InfraFixture infra)
{
    [Fact]
    public async Task Concurrent_views_all_count()
    {
        var db = await infra.CloneMainAsync();
        await using var app = App.Create(db);
        var client = app.CreateClient();
        var before = await client.ViewsAsync(1);

        var codes = await Concurrently.RunAsync(200, _ => client.ViewAsync(1));

        Assert.All(codes, c => Assert.Equal(HttpStatusCode.NoContent, c));
        Assert.Equal(before + 200, await client.ViewsAsync(1));
    }

    [Fact]
    public async Task A_missing_video_is_404()
    {
        await using var app = App.Create(await infra.CloneMainAsync());
        var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, await client.ViewAsync(999_999));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/videos/999999")).StatusCode);
    }

    [Fact]
    public async Task Top_lists_the_most_viewed_first()
    {
        await using var app = App.Create(await infra.CloneMainAsync());

        var top = await app.CreateClient().TopAsync(5);

        Assert.Equal(5, top.Count);
        Assert.Equal(top.OrderByDescending(v => v.Views).ThenBy(v => v.Id), top);
    }
}
