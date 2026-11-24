namespace ScalingReads;

// Every SQL statement the app sends, in one place, so you can paste them into EXPLAIN ANALYZE.
// Stage 1 is yours: change these (and add migrations) until the plans are cheap.
public static class PostQueries
{
    // Counts the likes table for every post on the page.
    public const string UserPosts = """
        SELECT p.id, p.author_id, u.name AS author_name, p.caption, p.created_at,
               (SELECT count(*) FROM likes l WHERE l.post_id = p.id) AS like_count
        FROM posts p
        JOIN users u ON u.id = p.author_id
        WHERE p.author_id = @userId
        ORDER BY p.created_at DESC
        LIMIT @limit
        """;

    public const string Post = """
        SELECT p.id, p.author_id, u.name AS author_name, p.caption, p.created_at,
               (SELECT count(*) FROM likes l WHERE l.post_id = p.id) AS like_count
        FROM posts p
        JOIN users u ON u.id = p.author_id
        WHERE p.id = @id
        """;

    public const string LikeCount = """
        SELECT count(*) FROM likes WHERE post_id = @id
        """;

    public const string CreatePost = """
        INSERT INTO posts (author_id, caption) VALUES (@userId, @caption)
        RETURNING id, created_at
        """;

    // Returns 1 if the like is new, 0 if this user had already liked the post.
    public const string Like = """
        INSERT INTO likes (post_id, user_id) VALUES (@postId, @userId)
        ON CONFLICT DO NOTHING
        """;
}
