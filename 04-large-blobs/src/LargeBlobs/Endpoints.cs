using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace LargeBlobs;

public static class Endpoints
{
    // There is no auth. The caller says who they are in this header.
    public const string UserHeader = "X-User-Id";

    public static void MapEndpoints(this WebApplication app)
    {
        // The baseline upload: the whole body into memory, then into MinIO.
        app.MapPut("/files/direct/{name}", async (string name, HttpRequest request, FileRepository files, Blobs blobs, Stats stats, CancellationToken ct) =>
        {
            if (UserId(request.HttpContext) is not { } owner)
            {
                return NoUser();
            }
            var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            stats.ThroughApi(buffer.Length);
            var contentType = request.ContentType ?? "application/octet-stream";
            var id = await files.InsertAsync(owner, name, contentType, buffer.Length, "pending", ct);
            buffer.Position = 0;
            await blobs.S3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = blobs.Bucket,
                Key = Blobs.KeyFor(id),
                InputStream = buffer,
                ContentType = contentType,
            }, ct);
            await files.MarkReadyAsync(id, ct);
            return Results.Created($"/files/{id}", await files.GetAsync(id, ct));
        });

        app.MapGet("/files/{id:long}", async (long id, FileRepository files, CancellationToken ct) =>
            await files.GetAsync(id, ct) is { } file ? Results.Ok(file) : Results.NotFound());

        // The baseline download: MinIO to the API to the client.
        app.MapGet("/files/{id:long}/content", async (long id, HttpResponse response, FileRepository files, Blobs blobs, Stats stats, CancellationToken ct) =>
        {
            if (await files.GetAsync(id, ct) is not { Status: "ready" } file)
            {
                return Results.NotFound();
            }
            using var obj = await blobs.S3.GetObjectAsync(blobs.Bucket, file.Key, ct);
            response.ContentType = file.ContentType;
            response.ContentLength = obj.ContentLength;
            await obj.ResponseStream.CopyToAsync(response.Body, ct);
            stats.ThroughApi(obj.ContentLength);
            return Results.Empty;
        });

        // Stage 1.
        app.MapPost("/files", async (NewFile file, HttpContext http, PresignedUploads uploads, CancellationToken ct) =>
        {
            if (UserId(http) is not { } owner)
            {
                return NoUser();
            }
            if (file.Size <= 0 || string.IsNullOrWhiteSpace(file.Name) || string.IsNullOrWhiteSpace(file.ContentType))
            {
                return Results.Problem("name, contentType and a positive size are required", statusCode: 400);
            }
            var ticket = await uploads.CreateAsync(owner, file, ct);
            return Results.Created($"/files/{ticket.Id}", ticket);
        });

        app.MapPost("/files/{id:long}/complete", async (long id, PresignedUploads uploads, FileRepository files, CancellationToken ct) =>
            Completed(await uploads.CompleteAsync(id, ct), await files.GetAsync(id, ct)));

        // Stage 2.
        app.MapPost("/files/{id:long}/multipart", async (long id, MultipartUploads uploads, CancellationToken ct) =>
            await uploads.StartAsync(id, ct) is { } plan ? Results.Ok(plan) : Results.NotFound());

        app.MapGet("/files/{id:long}/multipart/parts", async (long id, MultipartUploads uploads, CancellationToken ct) =>
            await uploads.ListPartsAsync(id, ct) is { } parts ? Results.Ok(parts) : Results.NotFound());

        app.MapPost("/files/{id:long}/multipart/complete", async (long id, MultipartUploads uploads, FileRepository files, CancellationToken ct) =>
            Completed(await uploads.CompleteAsync(id, ct), await files.GetAsync(id, ct)));

        app.MapDelete("/files/{id:long}/multipart", async (long id, MultipartUploads uploads, CancellationToken ct) =>
            await uploads.AbortAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // Stage 3: MinIO's bucket notifications.
        app.MapPost("/events/minio", async (S3Event e, HttpRequest request, ObjectCreatedHandler handler, IOptions<BlobsOptions> options, CancellationToken ct) =>
        {
            var token = options.Value.WebhookToken;
            if (token.Length > 0 && request.Headers.Authorization != $"Bearer {token}")
            {
                return Results.Unauthorized();
            }
            return Results.Ok(new { Changed = await handler.HandleAsync(e, ct) });
        });

        // Stage 4: sweep now instead of waiting for the loop.
        app.MapPost("/admin/sweep", async (OrphanSweeper sweeper, CancellationToken ct) => Results.Ok(await sweeper.SweepAsync(ct)));

        // Stage 5.
        app.MapGet("/files/{id:long}/download", async (long id, HttpContext http, DownloadLinks links, CancellationToken ct) =>
        {
            if (UserId(http) is not { } requester)
            {
                return NoUser();
            }
            var link = await links.CreateAsync(id, requester, ct);
            return link.Outcome switch
            {
                DownloadOutcome.Ok => Results.Redirect(link.Url!),
                DownloadOutcome.NotFound => Results.NotFound(),
                DownloadOutcome.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.Conflict(link),
            };
        });

        app.MapGet("/stats", (Stats stats) => Results.Ok(stats.Snapshot()));
    }

    private static IResult Completed(CompleteResult result, FileRecord? file) => result switch
    {
        CompleteResult.Ready => Results.Ok(file),
        CompleteResult.NotFound => Results.NotFound(),
        _ => Results.Conflict(new { Result = result.ToString() }),
    };

    private static IResult NoUser() => Results.Problem($"{UserHeader} header is required", statusCode: 401);

    private static long? UserId(HttpContext http) =>
        long.TryParse(http.Request.Headers[UserHeader], out var id) ? id : null;
}
