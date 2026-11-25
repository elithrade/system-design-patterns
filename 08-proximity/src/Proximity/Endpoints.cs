namespace Proximity;

public sealed record LocationReport(double Lat, double Lng, bool Available);

public static class Endpoints
{
    public static void MapEndpoints(this WebApplication app)
    {
        app.MapGet("/drivers/nearby", async (double lat, double lng, double? radiusM, int? limit, IDriverIndex index, CancellationToken ct) =>
            Results.Ok(await index.NearbyAsync(lat, lng, radiusM ?? 2000, Math.Clamp(limit ?? 10, 1, 1000), ct)));

        // One driver's report.
        app.MapPost("/drivers/{id:long}/location", async (long id, LocationReport report, IDriverIndex index, CancellationToken ct) =>
        {
            await index.UpsertManyAsync([new DriverLocation(id, report.Lat, report.Lng, report.Available)], ct);
            return Results.NoContent();
        });

        // Many reports at once, as a location-ingest service would forward them.
        app.MapPost("/drivers/locations", async (List<DriverLocation> reports, IDriverIndex index, CancellationToken ct) =>
        {
            await index.UpsertManyAsync(reports, ct);
            return Results.NoContent();
        });

        // Stages 3-4: copy every driver from Postgres into the index (Redis starts empty).
        app.MapPost("/admin/load", async (DriverStore store, IDriverIndex index, CancellationToken ct) =>
        {
            var count = 0;
            await foreach (var page in store.AllAsync(5000, ct))
            {
                await index.UpsertManyAsync(page, ct);
                count += page.Count;
            }
            return Results.Ok(new { loaded = count });
        });

        // Stage 4.
        app.MapPost("/admin/prune", async (IServiceProvider services, CancellationToken ct) =>
            services.GetService<RedisDriverIndex>() is { } redis
                ? Results.Ok(new { removed = await redis.PruneAsync(ct) })
                : Results.Problem("prune needs Proximity:Engine=Redis", statusCode: 501));
    }
}
