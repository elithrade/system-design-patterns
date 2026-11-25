using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace LargeBlobs;

// The bucket, and the S3 client that talks to MinIO. Your code uses S3 directly: presigning,
// multipart uploads and listing are all on IAmazonS3.
public sealed class Blobs(IAmazonS3 s3, IOptions<BlobsOptions> options)
{
    public IAmazonS3 S3 { get; } = s3;
    public string Bucket { get; } = options.Value.Bucket;

    // Where a file's bytes live, and back. Null for a key that is not one of ours.
    public static string KeyFor(long fileId) => $"files/{fileId}";

    public static long? IdFromKey(string key) =>
        key.StartsWith("files/", StringComparison.Ordinal) && long.TryParse(key.AsSpan(6), out var id) ? id : null;

    // At startup: create the bucket if needed, and (stage 3) subscribe it to MinIO's webhook target.
    public async Task EnsureBucketAsync(bool notifications, CancellationToken ct)
    {
        try
        {
            await S3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, ct);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }

        if (notifications)
        {
            // arn:minio:sqs::PRIMARY:webhook names the MINIO_NOTIFY_WEBHOOK_*_PRIMARY target in compose.yaml
            await S3.PutBucketNotificationAsync(new PutBucketNotificationRequest
            {
                BucketName = Bucket,
                QueueConfigurations =
                [
                    new QueueConfiguration
                    {
                        Queue = "arn:minio:sqs::PRIMARY:webhook",
                        Events = [EventType.ObjectCreatedAll],
                        Filter = new Filter { S3KeyFilter = new S3KeyFilter { FilterRules = [new FilterRule("prefix", "files/")] } },
                    },
                ],
            }, ct);
        }
    }
}
