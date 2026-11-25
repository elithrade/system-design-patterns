namespace ScalingReads;

// Every SQL statement the app sends, in one place, so you can paste them into EXPLAIN ANALYZE.
// Stage 1 is yours: change these (and add migrations) until the plans are cheap.
public static class PostQueries
{
    // Counts the likes table for every post on the page.
    public const string UserPosts = """
        SELECT p.id, p.author_id, u.name AS author_name, p.caption, p.created_at,
               p.like_count AS like_count
        FROM posts p
        JOIN users u ON u.id = p.author_id
        WHERE p.author_id = @userId
        ORDER BY p.created_at DESC
        LIMIT @limit
        """;

    public const string Post = """
        SELECT p.id, p.author_id, u.name AS author_name, p.caption, p.created_at,
               p.like_count AS like_count
        FROM posts p
        JOIN users u ON u.id = p.author_id
        WHERE p.id = @id
        """;

    public const string LikeCount = """
        SELECT like_count FROM posts WHERE id = @id
        """;

    public const string CreatePost = """
        INSERT INTO posts (author_id, caption) VALUES (@userId, @caption)
        RETURNING id, created_at
        """;

    // Returns 1 if the like is new, 0 if this user had already liked the post.
    public const string Like = """
        WITH new_like AS (
            INSERT INTO likes (post_id, user_id) VALUES (@postId, @userId)
            ON CONFLICT DO NOTHING RETURNING post_id
        )
        UPDATE posts SET like_count = like_count + 1
        WHERE id IN (SELECT post_id from new_like)
        """;
}
