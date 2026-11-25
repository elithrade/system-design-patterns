namespace LongRunning;

public static class Endpoints
{
    public const int DefaultYear = 2026;

    public static void MapEndpoints(this WebApplication app)
    {
        // The baseline: render inside the request. Through nginx, anything over 30 s is a 504.
        app.MapGet("/users/{id:long}/year-in-review", async (long id, int? year, Renderer renderer, CancellationToken ct) =>
            Results.File(await renderer.YearInReviewAsync(id, year ?? DefaultYear, ct), "application/pdf", $"stride-{year ?? DefaultYear}.pdf"));

        // Stage 1: queue it and answer at once.
        app.MapPost("/users/{id:long}/year-in-review", (long id, int? year, JobStore jobs, CancellationToken ct) =>
            EnqueueAsync(jobs, JobKinds.YearInReview, id, year ?? DefaultYear, ct));

        // A 200 ms job, both ways (the README's last question).
        app.MapGet("/users/{id:long}/summary", async (long id, int? year, Renderer renderer, CancellationToken ct) =>
            Results.Text(await renderer.SummaryAsync(id, year ?? DefaultYear, ct), "text/plain"));

        app.MapPost("/users/{id:long}/summary", (long id, int? year, JobStore jobs, CancellationToken ct) =>
            EnqueueAsync(jobs, JobKinds.Summary, id, year ?? DefaultYear, ct));

        app.MapGet("/jobs/{id:long}", async (long id, JobStore jobs, CancellationToken ct) =>
            await jobs.GetAsync(id, ct) is { } job ? Results.Ok(job) : Results.NotFound());

        app.MapGet("/jobs/{id:long}/result", async (long id, JobStore jobs, CancellationToken ct) =>
        {
            if (await jobs.GetAsync(id, ct) is not { } job)
            {
                return Results.NotFound();
            }
            if (await jobs.GetResultAsync(id, ct) is not { } result)
            {
                return Results.Conflict(job);
            }
            return job.Kind == JobKinds.YearInReview
                ? Results.File(result, "application/pdf", $"stride-{job.Year}.pdf")
                : Results.Text(result, "text/plain");
        });

        // Stage 4. 501 under the Naive queue.
        app.MapGet("/jobs/dead", async (IServiceProvider services, CancellationToken ct) =>
            services.GetService<SkipLockedQueue>() is { } queue ? Results.Ok(await queue.ListDeadAsync(ct)) : NoDeadLetters());

        app.MapPost("/jobs/{id:long}/retry", async (long id, IServiceProvider services, CancellationToken ct) =>
            services.GetService<SkipLockedQueue>() is not { } queue ? NoDeadLetters()
            : await queue.RequeueDeadAsync(id, ct) ? Results.Ok() : Results.NotFound());

        app.MapGet("/stats", (Stats stats) => Results.Ok(stats.Snapshot()));
    }

    private static async Task<IResult> EnqueueAsync(JobStore jobs, string kind, long userId, int year, CancellationToken ct)
    {
        var id = await jobs.EnqueueAsync(kind, userId, year, ct);
        return Results.Accepted($"/jobs/{id}", await jobs.GetAsync(id, ct));
    }

    private static IResult NoDeadLetters() => Results.Problem("dead letters need Jobs:Queue=SkipLocked", statusCode: 501);
}
