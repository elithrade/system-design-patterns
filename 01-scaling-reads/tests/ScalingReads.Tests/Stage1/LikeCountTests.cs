using Dapper;
using Npgsql;

namespace ScalingReads.Tests.Stage1;

// Guards for stage 1. They pass on the baseline and must keep passing after you denormalize:
// a stored like count is only useful if it is always right.
[Collection("infra")]
public sealed class LikeCountTests(InfraFixture infra)
{
    [Fact]
    public async Task Seeded_counts_match_the_likes_table()
    {
        var db = await infra.CloneAsync();
        await using var app = App.Create(infra, db);
        var client = app.CreateClient();

        for (long id = 1; id <= InfraFixture.Celebrities + 2; id++)
        {
            var post = await client.GetPostAsync(id);
            Assert.Equal(await CountLikesAsync(db, id), post.LikeCount);
        }
    }

    [Fact]
    public async Task Concurrent_likes_from_different_users_all_count()
    {
        var db = await infra.CloneAsync();
        await using var app = App.Create(infra, db);
        var post = await app.As(1).CreatePostAsync("concurrent");

        await Concurrently.RunAsync(100, i => app.As(1000 + i).LikeAsync(post.Id));

        Assert.Equal(100, (await app.CreateClient().GetPostAsync(post.Id)).LikeCount);
        Assert.Equal(100, await CountLikesAsync(db, post.Id));
    }

    [Fact]
    public async Task A_user_liking_twice_counts_once()
    {
        var db = await infra.CloneAsync();
        await using var app = App.Create(infra, db);
        var post = await app.As(1).CreatePostAsync("twice");

        await Concurrently.RunAsync(20, _ => app.As(7).LikeAsync(post.Id));
        await app.As(7).LikeAsync(post.Id);

        Assert.Equal(1, (await app.CreateClient().GetPostAsync(post.Id)).LikeCount);
    }

    [Fact]
    public async Task User_posts_shows_new_posts_first_with_their_counts()
    {
        var db = await infra.CloneAsync();
        await using var app = App.Create(infra, db);
        var author = app.As(42);
        var older = await author.CreatePostAsync("older");
        var newer = await author.CreatePostAsync("newer");
        await app.As(43).LikeAsync(older.Id);
        await app.As(44).LikeAsync(older.Id);

        var (posts, _) = await author.GetUserPostsAsync(42, limit: 2);

        Assert.Equal([newer.Id, older.Id], posts.Select(p => p.Id));
        Assert.Equal([0L, 2L], posts.Select(p => p.LikeCount));
        Assert.All(posts, p => Assert.Equal("user 42", p.AuthorName));
    }

    private static async Task<long> CountLikesAsync(string db, long postId)
    {
        await using var conn = new NpgsqlConnection(db);
        return await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM likes WHERE post_id = @postId", new { postId });
    }
}
