namespace ScalingReads.Tests.Stage2;

// Stage 2 goal: reads go to the replica, except that users see their own writes immediately.
// Here the "replica" is a separate copy of the seed data that never receives any write, so a read
// that lands on it can never see a new post. That is replication lag at its worst.
[Collection("infra")]
public sealed class ReadYourWritesTests(InfraFixture infra) : IAsyncLifetime
{
    private App _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = App.Create(infra, await infra.CloneAsync(), await infra.CloneAsync(),
            router: RouterKind.ReadYourWrites, stickySeconds: 5);

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task Reads_go_to_the_replica_by_default()
    {
        var (_, db) = await _app.As(42).GetUserPostsAsync(42);

        Assert.Equal("replica", db);
    }

    [Fact]
    public async Task Anonymous_reads_go_to_the_replica()
    {
        var (_, db) = await _app.CreateClient().GetUserPostsAsync(42);

        Assert.Equal("replica", db);
    }

    [Fact]
    public async Task Authors_see_their_new_post_immediately()
    {
        var alice = _app.As(42);
        var post = await alice.CreatePostAsync("hello");

        var (posts, db) = await alice.GetUserPostsAsync(42);

        Assert.Equal("primary", db);
        Assert.Equal(post.Id, posts[0].Id);
    }

    [Fact]
    public async Task Other_users_still_read_the_replica()
    {
        await _app.As(42).CreatePostAsync("hello");

        var (_, db) = await _app.As(43).GetUserPostsAsync(42);

        Assert.Equal("replica", db);
    }

    [Fact]
    public async Task A_like_also_counts_as_a_write()
    {
        var bob = _app.As(43);
        await bob.LikeAsync(10);

        var post = await bob.GetPostAsync(10);
        var (_, db) = await bob.GetUserPostsAsync(post.AuthorId);

        Assert.Equal("primary", db);
    }

    [Fact]
    public async Task Reads_return_to_the_replica_after_the_sticky_window()
    {
        var alice = _app.As(42);
        await alice.CreatePostAsync("hello");

        _app.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal("primary", (await alice.GetUserPostsAsync(42)).Db);

        _app.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("replica", (await alice.GetUserPostsAsync(42)).Db);
    }

    [Fact]
    public async Task A_new_write_restarts_the_window()
    {
        var alice = _app.As(42);
        await alice.CreatePostAsync("first");
        _app.Time.Advance(TimeSpan.FromSeconds(4));
        await alice.CreatePostAsync("second");

        _app.Time.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal("primary", (await alice.GetUserPostsAsync(42)).Db);
    }
}
