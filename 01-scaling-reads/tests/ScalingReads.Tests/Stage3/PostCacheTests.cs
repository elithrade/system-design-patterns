using System.Net;
using Dapper;
using Npgsql;

namespace ScalingReads.Tests.Stage3;

// Stage 3 goal: GET /posts/{id} is served from Redis, and a like keeps it right without evicting it.
[Collection("infra")]
public sealed class PostCacheTests(InfraFixture infra) : IAsyncLifetime
{
    private string _db = null!;
    private App _app = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await infra.CloneAsync();
        _app = App.Create(infra, _db, cache: CacheKind.Redis);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task A_cached_post_matches_the_database_exactly()
    {
        var client = _app.CreateClient();
        var fromDb = await _app.Services.GetRequiredService<PostRepository>()
            .GetPostAsync(5, DbTarget.Primary, CancellationToken.None);

        await client.GetPostAsync(5);
        var cached = await client.GetPostAsync(5);

        Assert.Equal(fromDb, cached);
    }

    [Fact]
    public async Task Repeated_reads_hit_the_database_once()
    {
        var client = _app.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            await client.GetPostAsync(5);
        }

        Assert.Equal(1, _app.Stats.PostReads);
        Assert.True(_app.Stats.LikeCountReads <= 1, $"{_app.Stats.LikeCountReads} like count reads");
    }

    [Fact]
    public async Task A_new_like_shows_in_the_next_read()
    {
        var post = await _app.As(1).CreatePostAsync("fresh");
        var client = _app.CreateClient();
        await client.GetPostAsync(post.Id);

        for (var i = 1; i <= 5; i++)
        {
            await _app.As(100 + i).LikeAsync(post.Id);
            Assert.Equal(i, (await client.GetPostAsync(post.Id)).LikeCount);
        }
    }

    [Fact]
    public async Task Likes_do_not_evict_the_post()
    {
        var post = await _app.As(1).CreatePostAsync("popular");
        var client = _app.CreateClient();
        await client.GetPostAsync(post.Id);
        var before = _app.Stats.PostReads;

        for (var i = 1; i <= 10; i++)
        {
            await _app.As(100 + i).LikeAsync(post.Id);
            await client.GetPostAsync(post.Id);
        }

        Assert.Equal(before, _app.Stats.PostReads);
    }

    [Fact]
    public async Task A_like_on_a_post_nobody_has_read_yet_keeps_its_old_likes()
    {
        // post 1 has CelebrityLikes likes in the seed and is not in the cache yet
        await _app.As(InfraFixture.Users).LikeAsync(1);

        var post = await _app.CreateClient().GetPostAsync(1);

        Assert.Equal(await CountLikesAsync(1), post.LikeCount);
    }

    [Fact]
    public async Task Concurrent_likes_all_show_up()
    {
        var post = await _app.As(1).CreatePostAsync("contested");
        var client = _app.CreateClient();
        await client.GetPostAsync(post.Id);

        await Concurrently.RunAsync(50, i => _app.As(100 + i).LikeAsync(post.Id));

        Assert.Equal(50, (await client.GetPostAsync(post.Id)).LikeCount);
    }

    [Fact]
    public async Task A_missing_post_is_404()
    {
        var response = await _app.CreateClient().GetAsync("/posts/999999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<long> CountLikesAsync(long postId)
    {
        await using var conn = new NpgsqlConnection(_db);
        return await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM likes WHERE post_id = @postId", new { postId });
    }
}
