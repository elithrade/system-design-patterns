namespace ScalingReads.Tests.Stage4;

// Stage 4 goal: a hot post costs Redis almost nothing, and the price is bounded staleness.
[Collection("infra")]
public sealed class HotKeyTests(InfraFixture infra) : IAsyncLifetime
{
    private App _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = App.Create(infra, await infra.CloneAsync(), cache: CacheKind.RedisL1, l1Seconds: 1);

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task A_cold_stampede_loads_the_post_once()
    {
        var client = _app.CreateClient();

        var posts = await Concurrently.RunAsync(500, _ => client.GetPostAsync(1));

        Assert.All(posts, p => Assert.Equal(1, p.Id));
        Assert.Equal(1, _app.Stats.PostReads);
        Assert.True(_app.Stats.RedisCommands <= 10, $"{_app.Stats.RedisCommands} Redis commands for one post");
    }

    [Fact]
    public async Task When_the_local_copy_expires_one_request_goes_to_Redis()
    {
        var client = _app.CreateClient();
        await client.GetPostAsync(1);
        _app.Time.Advance(TimeSpan.FromSeconds(2));
        var before = _app.Stats.RedisCommands;

        await Concurrently.RunAsync(500, _ => client.GetPostAsync(1));

        var sent = _app.Stats.RedisCommands - before;
        Assert.True(sent is > 0 and <= 4, $"{sent} Redis commands after expiry (expected 1 to 4)");
    }

    [Fact]
    public async Task Within_the_window_reads_do_not_touch_Redis()
    {
        var client = _app.CreateClient();
        await client.GetPostAsync(2);
        var before = _app.Stats.RedisCommands;

        _app.Time.Advance(TimeSpan.FromMilliseconds(900));
        for (var i = 0; i < 20; i++)
        {
            await client.GetPostAsync(2);
        }

        Assert.Equal(before, _app.Stats.RedisCommands);
    }

    [Fact]
    public async Task A_like_shows_up_once_the_window_passes()
    {
        var post = await _app.As(1).CreatePostAsync("hot");
        var client = _app.CreateClient();
        Assert.Equal(0, (await client.GetPostAsync(post.Id)).LikeCount);

        await _app.As(2).LikeAsync(post.Id);
        _app.Time.Advance(TimeSpan.FromMilliseconds(1100));

        Assert.Equal(1, (await client.GetPostAsync(post.Id)).LikeCount);
    }

    [Fact]
    public async Task Different_posts_are_cached_separately()
    {
        var client = _app.CreateClient();

        var results = await Concurrently.RunAsync(60, i => client.GetPostAsync(1 + i % 3));

        Assert.All(results.Select((p, i) => (p, i)), x => Assert.Equal(1 + x.i % 3, x.p.Id));
    }
}
