using Microsoft.Extensions.Options;

namespace LargeBlobs;

public sealed record SweepReport(int RowsDeleted, int ObjectsDeleted, int UploadsAborted);

// Stage 4. Uploads get abandoned: a pending row whose bytes never came, a multipart upload with half
// its parts (MinIO keeps those parts, and you pay for them, until someone aborts it), an object
// whose row was deleted. Nothing ever cleans them up unless something sweeps.
//
// SweepAsync removes everything older than PendingHours (by the injected TimeProvider):
//   - pending rows: abort the file's multipart upload if it has one, delete its object if there is
//     one, delete the row
//   - multipart uploads in the bucket that no pending row owns
//   - objects under files/ that have no row at all
// It never touches ready or failed files, or anything younger than PendingHours.
// Running it again right away finds nothing.
//
// The goal, which the tests check: each of the cases above, counted in the report.
//
// Objects and uploads carry MinIO's timestamps (LastModified, Initiated); rows carry created_at.
// Compare both with time.GetUtcNow(). ListObjectsV2 and ListMultipartUploads return pages: follow
// them. In this SDK version an empty list can come back as null. MinIO only honors a Prefix on
// ListMultipartUploads that is a whole object key: list without one and filter by key yourself.
//
// Tests: dotnet test --filter-namespace "*Stage4"
public sealed class OrphanSweeper(FileRepository files, Blobs blobs, IOptions<BlobsOptions> options, TimeProvider time)
{
    private readonly TimeSpan _maxAge = TimeSpan.FromHours(options.Value.PendingHours);

    public Task<SweepReport> SweepAsync(CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}

// Runs the sweeper every SweepMinutes. POST /admin/sweep runs it once, now.
public sealed class SweepLoop(OrphanSweeper sweeper, IOptions<BlobsOptions> options, TimeProvider time, ILogger<SweepLoop> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.SweepMinutes), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var report = await sweeper.SweepAsync(stoppingToken);
                    logger.LogInformation("Swept {Report}", report);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Sweep failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
