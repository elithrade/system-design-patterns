using Npgsql;

namespace ScalingReads;

public sealed record CreatePostRequest(string Caption);
public sealed record CreatedPost(long Id, DateTime CreatedAt);

public static class Endpoints
{
    // There is no auth. The caller says who they are in this header; reads work without it.
    public const string UserHeader = "X-User-Id";

    // Every read response says which database served it, so you can watch the router work.
    public const string DbHeader = "X-Db";

    public static void MapEndpoints(this WebApplication app)
    {
        // A user's profile grid: their posts, newest first, each with author and like count.
        app.MapGet("/users/{userId:long}/posts", async (
            long userId, int? limit, HttpContext http,
            IConnectionRouter router, PostRepository posts, CancellationToken ct) =>
        {
            var target = router.ForRead(ViewerId(http));
            http.Response.Headers[DbHeader] = target.ToString().ToLowerInvariant();
            return Results.Ok(await posts.GetUserPostsAsync(userId, Math.Clamp(limit ?? 20, 1, 100), target, ct));
        });

        // One post. Goes through IPostReader, which is where the caches plug in (stages 3 and 4).
        app.MapGet("/posts/{id:long}", async (
            long id, HttpContext http, IConnectionRouter router, IPostReader reader, CancellationToken ct) =>
        {
            var target = router.ForRead(ViewerId(http));
            http.Response.Headers[DbHeader] = target.ToString().ToLowerInvariant();
            var post = await reader.GetPostAsync(id, target, ct);
            return post is null ? Results.NotFound() : Results.Ok(post);
        });

        app.MapPost("/posts", async (
            CreatePostRequest request, HttpContext http,
            IConnectionRouter router, PostRepository posts, CancellationToken ct) =>
        {
            if (ViewerId(http) is not { } userId)
            {
                return Results.Problem($"{UserHeader} header is required", statusCode: 401);
            }
            try
            {
                var created = await posts.CreatePostAsync(userId, request.Caption, ct);
                router.RecordWrite(userId);
                return Results.Created($"/posts/{created.Id}", created);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.Problem($"user {userId} does not exist", statusCode: 404);
            }
        });

        // Liking twice is a no-op, not an error.
        app.MapPost("/posts/{id:long}/likes", async (
            long id, HttpContext http,
            IConnectionRouter router, PostRepository posts, IPostChangeListener listener, CancellationToken ct) =>
        {
            if (ViewerId(http) is not { } userId)
            {
                return Results.Problem($"{UserHeader} header is required", statusCode: 401);
            }
            try
            {
                var added = await posts.LikeAsync(id, userId, ct);
                router.RecordWrite(userId);
                if (added)
                {
                    await listener.LikedAsync(id, ct);
                }
                return Results.NoContent();
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.NotFound();
            }
        });

        // Counters since the app started. k6 prints the difference across a run.
        app.MapGet("/stats", (Stats stats) => Results.Ok(stats.Snapshot()));
    }

    private static long? ViewerId(HttpContext http) =>
        long.TryParse(http.Request.Headers[UserHeader], out var id) ? id : null;
}
