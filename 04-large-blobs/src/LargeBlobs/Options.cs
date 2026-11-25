namespace LargeBlobs;

// The "Blobs" configuration section. Set from the command line with Blobs__PartSizeMb=16 etc.
public sealed class BlobsOptions
{
    public const string Section = "Blobs";

    // The bucket the app creates at startup and keeps every file in. The tests give each test its own.
    public string Bucket { get; set; } = "uploads";

    // Stage 1: how long a presigned upload URL works.
    public int UploadUrlSeconds { get; set; } = 900;

    // Stage 2: the size of every part but the last. S3 and MinIO refuse parts under 5 MiB.
    public int PartSizeMb { get; set; } = 8;

    // Stage 4: a pending upload older than this is abandoned.
    public int PendingHours { get; set; } = 24;

    // Stage 4: run the sweeper in the background every SweepMinutes. The tests turn it off.
    public bool Sweeper { get; set; } = true;
    public int SweepMinutes { get; set; } = 60;

    // Stage 5: how long a download link works.
    public int DownloadSeconds { get; set; } = 300;

    // Stage 3: ask MinIO at startup to send this bucket's ObjectCreated events to the webhook target
    // compose.yaml configures. The tests turn it off: MinIO cannot reach a test server.
    public bool Notifications { get; set; } = true;

    // Stage 3: the bearer token MinIO sends with each event (compose.yaml). Empty: no check.
    public string WebhookToken { get; set; } = "";
}

// The "S3" section: where MinIO is.
public sealed class S3Options
{
    public const string Section = "S3";

    public string ServiceUrl { get; set; } = "http://localhost:59000";
    public string AccessKey { get; set; } = "minio";
    public string SecretKey { get; set; } = "minio-secret";
}
