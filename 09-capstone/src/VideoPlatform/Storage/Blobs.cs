using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace VideoPlatform;

// The bucket, and the S3 client that talks to MinIO.
public sealed class Blobs(IAmazonS3 s3, IOptions<StorageOptions> options)
{
    public IAmazonS3 S3 { get; } = s3;
    public string Bucket { get; } = options.Value.Bucket;

    public static string SourceKey(long videoId) => $"videos/{videoId}/source";
    public static string RenditionKey(long videoId, string rendition) => $"videos/{videoId}/{rendition}";

    public async Task EnsureBucketAsync(CancellationToken ct)
    {
        try
        {
            await S3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, ct);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
    }

    // The object's size, or null if there is no object.
    public async Task<long?> SizeAsync(string key, CancellationToken ct)
    {
        try
        {
            return (await S3.GetObjectMetadataAsync(Bucket, key, ct)).ContentLength;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // Deletes the objects under the video's prefix whose names are given (missing ones are fine).
    public async Task DeleteAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        foreach (var key in keys)
        {
            await S3.DeleteObjectAsync(Bucket, key, ct);
        }
    }

    // A download link for a published rendition.
    public string DownloadUrl(string key, DateTimeOffset expires) => S3.GetPreSignedURL(new GetPreSignedUrlRequest
    {
        BucketName = Bucket,
        Key = key,
        Verb = HttpVerb.GET,
        Expires = expires.UtcDateTime,
        Protocol = Protocol.HTTP,
    });
}
