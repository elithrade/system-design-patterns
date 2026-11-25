using System.Net.Http.Json;
using Amazon.S3.Model;

namespace LargeBlobs.Tests.Stage4;

// Stage 4 goal: abandoned uploads are cleaned up, and nothing live is touched.
// Rows are inserted directly and objects put straight into MinIO, so these tests do not depend on
// your stage 1-3 code.
[Collection("infra")]
public sealed class SweeperTests(InfraFixture infra)
{
    private static async Task<SweepReport> SweepAsync(App app)
    {
        var response = await app.As(1).PostAsync("/admin/sweep", null);
        Assert.True(response.IsSuccessStatusCode, $"sweep: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<SweepReport>())!;
    }

    private static Task<long> InsertAsync(App app, string status, long size = 10) =>
        app.Get<FileRepository>().InsertAsync(1, "f.bin", "application/octet-stream", size, status, CancellationToken.None);

    private async Task<string> StartUploadWithOnePartAsync(App app, string key)
    {
        var upload = await infra.S3.InitiateMultipartUploadAsync(app.Bucket, key);
        await infra.S3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = app.Bucket,
            Key = key,
            UploadId = upload.UploadId,
            PartNumber = 1,
            InputStream = new MemoryStream(Bytes.Make(5 * Bytes.MiB)),
        });
        return upload.UploadId;
    }

    private async Task<bool> ExistsAsync(App app, string key) =>
        (await infra.ObjectsAsync(app.Bucket)).Any(o => o.Key == key);

    [Fact]
    public async Task An_abandoned_pending_file_and_its_object_are_removed()
    {
        await using var app = await App.StartAsync(infra);
        var id = await InsertAsync(app, "pending");
        await infra.PutObjectAsync(app.Bucket, Blobs.KeyFor(id), new byte[10]);
        app.Time.Advance(TimeSpan.FromHours(25));

        var report = await SweepAsync(app);

        Assert.Equal(new SweepReport(RowsDeleted: 1, ObjectsDeleted: 1, UploadsAborted: 0), report);
        Assert.Null(await app.Get<FileRepository>().GetAsync(id, CancellationToken.None));
        Assert.False(await ExistsAsync(app, Blobs.KeyFor(id)));
    }

    [Fact]
    public async Task An_abandoned_multipart_upload_is_aborted()
    {
        await using var app = await App.StartAsync(infra);
        var id = await InsertAsync(app, "pending", size: 12 * Bytes.MiB);
        var uploadId = await StartUploadWithOnePartAsync(app, Blobs.KeyFor(id));
        await app.Get<FileRepository>().SetUploadIdAsync(id, uploadId, CancellationToken.None);
        app.Time.Advance(TimeSpan.FromHours(25));

        var report = await SweepAsync(app);

        Assert.Equal(new SweepReport(RowsDeleted: 1, ObjectsDeleted: 0, UploadsAborted: 1), report);
        Assert.Empty(await infra.OpenUploadsAsync(app.Bucket));
    }

    [Fact]
    public async Task Uploads_and_objects_with_no_row_are_removed()
    {
        await using var app = await App.StartAsync(infra);
        await StartUploadWithOnePartAsync(app, Blobs.KeyFor(777_777));
        await infra.PutObjectAsync(app.Bucket, Blobs.KeyFor(888_888), new byte[10]);
        app.Time.Advance(TimeSpan.FromHours(25));

        var report = await SweepAsync(app);

        Assert.Equal(new SweepReport(RowsDeleted: 0, ObjectsDeleted: 1, UploadsAborted: 1), report);
        Assert.Empty(await infra.OpenUploadsAsync(app.Bucket));
        Assert.Empty(await infra.ObjectsAsync(app.Bucket));
    }

    [Fact]
    public async Task Anything_younger_than_the_limit_stays()
    {
        await using var app = await App.StartAsync(infra);
        var id = await InsertAsync(app, "pending");
        await infra.PutObjectAsync(app.Bucket, Blobs.KeyFor(id), new byte[10]);
        await infra.PutObjectAsync(app.Bucket, Blobs.KeyFor(888_888), new byte[10]);
        await StartUploadWithOnePartAsync(app, Blobs.KeyFor(777_777));
        app.Time.Advance(TimeSpan.FromHours(23));

        var report = await SweepAsync(app);

        Assert.Equal(new SweepReport(0, 0, 0), report);
        Assert.NotNull(await app.Get<FileRepository>().GetAsync(id, CancellationToken.None));
        Assert.Equal(2, (await infra.ObjectsAsync(app.Bucket)).Count);
        Assert.Single(await infra.OpenUploadsAsync(app.Bucket));
    }

    [Fact]
    public async Task Ready_and_failed_files_and_other_prefixes_stay()
    {
        await using var app = await App.StartAsync(infra);
        var ready = await InsertAsync(app, "ready");
        var failed = await InsertAsync(app, "failed");
        await infra.PutObjectAsync(app.Bucket, Blobs.KeyFor(ready), new byte[10]);
        await infra.PutObjectAsync(app.Bucket, Blobs.KeyFor(failed), new byte[10]);
        await infra.PutObjectAsync(app.Bucket, "avatars/7.png", new byte[10]);
        app.Time.Advance(TimeSpan.FromHours(48));

        var report = await SweepAsync(app);

        Assert.Equal(new SweepReport(0, 0, 0), report);
        Assert.Equal(3, (await infra.ObjectsAsync(app.Bucket)).Count);
    }

    [Fact]
    public async Task A_second_sweep_finds_nothing()
    {
        await using var app = await App.StartAsync(infra);
        var id = await InsertAsync(app, "pending");
        await infra.PutObjectAsync(app.Bucket, Blobs.KeyFor(id), new byte[10]);
        await infra.PutObjectAsync(app.Bucket, Blobs.KeyFor(888_888), new byte[10]);
        app.Time.Advance(TimeSpan.FromHours(25));
        await SweepAsync(app);

        Assert.Equal(new SweepReport(0, 0, 0), await SweepAsync(app));
    }
}
