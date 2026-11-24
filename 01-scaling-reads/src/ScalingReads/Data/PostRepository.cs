using Dapper;

namespace ScalingReads;

// The database, with no cache. Reads go wherever the caller says; writes always go to the primary.
public sealed class PostRepository(DataSources db, Stats stats) : IPostReader
{
    // author_name -> AuthorName
    static PostRepository() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public async Task<IReadOnlyList<Post>> GetUserPostsAsync(long userId, int limit, DbTarget target, CancellationToken ct)
    {
        stats.Query(target);
        await using var conn = await db.For(target).OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Post>(new CommandDefinition(
            PostQueries.UserPosts, new { userId, limit }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<Post?> GetPostAsync(long id, DbTarget target, CancellationToken ct)
    {
        stats.Query(target);
        stats.PostRead();
        await using var conn = await db.For(target).OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Post>(new CommandDefinition(
            PostQueries.Post, new { id }, cancellationToken: ct));
    }

    public async Task<long> GetLikeCountAsync(long id, DbTarget target, CancellationToken ct)
    {
        stats.Query(target);
        stats.LikeCountRead();
        await using var conn = await db.For(target).OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            PostQueries.LikeCount, new { id }, cancellationToken: ct));
    }

    public async Task<CreatedPost> CreatePostAsync(long userId, string caption, CancellationToken ct)
    {
        stats.Query(DbTarget.Primary);
        await using var conn = await db.Primary.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<CreatedPost>(new CommandDefinition(
            PostQueries.CreatePost, new { userId, caption }, cancellationToken: ct));
    }

    // Returns true if the like is new, false if this user had already liked the post.
    // Stage 1: once posts has a like_count column, keep it right here, in the same transaction.
    public async Task<bool> LikeAsync(long postId, long userId, CancellationToken ct)
    {
        stats.Query(DbTarget.Primary);
        await using var conn = await db.Primary.OpenConnectionAsync(ct);
        var inserted = await conn.ExecuteAsync(new CommandDefinition(
            PostQueries.Like, new { postId, userId }, cancellationToken: ct));
        return inserted == 1;
    }
}
