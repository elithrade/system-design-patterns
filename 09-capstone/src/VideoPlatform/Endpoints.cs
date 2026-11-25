using Microsoft.Extensions.Options;

namespace VideoPlatform;

public static class Endpoints
{
    public static void MapEndpoints(this WebApplication app)
    {
        // The baseline: the body is the video. The API holds all of it, writes it to MinIO, then
        // makes every rendition before answering.
        app.MapPut("/videos/direct", async (HttpRequest request, string title, VideoStore videos, Blobs blobs,
            InlinePipeline inline, Stats stats, CancellationToken ct) =>
        {
            if (!TryOwner(request, out var owner))
            {
                return Results.BadRequest("X-User-Id header required");
            }
            var body = new MemoryStream();
            await request.Body.CopyToAsync(body, ct);
            stats.BytesThroughApi(body.Length);
            body.Position = 0;
            var video = await videos.CreateAsync(owner, title, request.ContentType ?? "video/mp4", body.Length, ct);
            await blobs.S3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
            {
                BucketName = blobs.Bucket,
                Key = Blobs.SourceKey(video.Id),
                InputStream = body,
                ContentType = video.ContentType,
            }, ct);
            await videos.SetStatusAsync(video.Id, [VideoStatus.Pending], VideoStatus.Uploaded, null, ct);
            await inline.OnUploadedAsync(video.Id, ct);
            return Results.Created($"/videos/{video.Id}", await videos.SnapshotAsync(video.Id, ct));
        });

        // Stage 1: a pending video and a presigned PUT URL.
        app.MapPost("/videos", async (HttpRequest request, NewVideo body, VideoUploads uploads, IPipeline pipeline, CancellationToken ct) =>
        {
            if (!TryOwner(request, out var owner))
            {
                return Results.BadRequest("X-User-Id header required");
            }
            UploadTicket ticket;
            try
            {
                ticket = await uploads.CreateAsync(owner, body, ct);
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(e.Message);
            }
            await pipeline.OnCreatedAsync(ticket.Id, ct);
            return Results.Created($"/videos/{ticket.Id}", ticket);
        });

        app.MapPost("/videos/{id:long}/uploaded", async (long id, VideoUploads uploads, IPipeline pipeline, VideoStore videos, CancellationToken ct) =>
        {
            var result = await uploads.ConfirmAsync(id, ct);
            switch (result)
            {
                case ConfirmResult.Confirmed or ConfirmResult.AlreadyConfirmed:
                    await pipeline.OnUploadedAsync(id, ct);
                    return Results.Accepted($"/videos/{id}", await videos.SnapshotAsync(id, ct));
                case ConfirmResult.NotFound:
                    return Results.NotFound();
                default:
                    return Results.Conflict(new { result = result.ToString() });
            }
        });

        app.MapGet("/videos/{id:long}", async (long id, VideoStore videos, CancellationToken ct) =>
            await videos.SnapshotAsync(id, ct) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());

        // Stage 3: the progress bar.
        app.MapGet("/videos/{id:long}/progress", async (long id, HttpResponse response, ProgressStream stream, Stats stats, CancellationToken ct) =>
        {
            stats.StreamOpened();
            try
            {
                await stream.RunAsync(response, id, ct);
            }
            finally
            {
                stats.StreamClosed();
            }
        });

        // A published rendition: a redirect to a presigned GET.
        app.MapGet("/videos/{id:long}/renditions/{name}", async (long id, string name, VideoStore videos, Blobs blobs, TimeProvider time,
            IOptions<TranscodeOptions> transcode, CancellationToken ct) =>
        {
            var video = await videos.GetAsync(id, ct);
            if (video is null || video.Status != VideoStatus.Published || !transcode.Value.Renditions.ContainsKey(name))
            {
                return Results.NotFound();
            }
            return Results.Redirect(blobs.DownloadUrl(Blobs.RenditionKey(id, name), time.GetUtcNow().AddMinutes(5)));
        });

        app.MapGet("/stats", (Stats stats) => Results.Ok(stats.Snapshot()));
    }

    private static bool TryOwner(HttpRequest request, out long owner) =>
        long.TryParse(request.Headers["X-User-Id"], out owner);
}
