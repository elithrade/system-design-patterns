namespace ScalingWrites;

public static class Endpoints
{
    public static void MapEndpoints(this WebApplication app)
    {
        // A video and its view count.
        app.MapGet("/videos/{id:long}", async (long id, VideoRepository videos, IViewCounter counter, CancellationToken ct) =>
        {
            var video = await videos.GetAsync(id, ct);
            if (video is null)
            {
                return Results.NotFound();
            }
            return Results.Ok(new { video.Id, video.Title, Views = await counter.GetViewsAsync(video, ct) });
        });

        // One view. The status code says what happened to it (see RecordResult).
        app.MapPost("/videos/{id:long}/views", async (long id, IViewCounter counter, HttpContext http, CancellationToken ct) =>
        {
            switch (await counter.RecordAsync(id, ct))
            {
                case RecordResult.Written:
                    return Results.NoContent();
                case RecordResult.Accepted:
                    return Results.Accepted();
                case RecordResult.NotFound:
                    return Results.NotFound();
                default:
                    http.Response.Headers.RetryAfter = "1";
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });

        // The most viewed videos. 501 for the Sharded counter, which has no way to rank them.
        app.MapGet("/videos/top", async (int? limit, IServiceProvider services, CancellationToken ct) =>
        {
            var top = services.GetService<ITopVideos>();
            if (top is null)
            {
                return Results.Problem("this counter has no top list", statusCode: StatusCodes.Status501NotImplemented);
            }
            return Results.Ok(await top.TopAsync(Math.Clamp(limit ?? 10, 1, 100), ct));
        });

        // Counters since the app started. k6 prints the difference across a run.
        app.MapGet("/stats", (Stats stats) => Results.Ok(stats.Snapshot()));
    }
}
