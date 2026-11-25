using Amazon.S3;
using Microsoft.Extensions.Options;

namespace LargeBlobs;

// What POST /files sends: the file the client is about to upload.
public sealed record NewFile(string Name, string ContentType, long Size);

// What POST /files answers: where to PUT the bytes, and until when.
public sealed record UploadTicket(long Id, string UploadUrl, DateTimeOffset ExpiresAt);

public enum CompleteResult
{
    // the bytes are in MinIO with the declared size; the file is ready
    Ready,
    // no such file
    NotFound,
    // the file is not pending (already ready, or failed)
    NotPending,
    // no object in MinIO yet
    Missing,
    // the object's size is not the declared size; the file stays pending
    SizeMismatch,
}

// Stage 1. The client uploads straight to MinIO with a presigned PUT URL; the API only signs.
//
// CreateAsync: insert a pending row (files.InsertAsync), then presign a PUT for its key
// (Blobs.KeyFor) that is valid for UploadUrlSeconds and only for the declared content type.
// CompleteAsync: the client says it has finished. Check MinIO (a HEAD, not a download) and mark
// the file ready if the object is there with the declared size.
//
// The goal, which the tests check:
//   - a client can PUT the bytes to UploadUrl with that Content-Type, complete, and the file is
//     ready; the API reads none of the bytes (Stats.BytesThroughApi stays 0)
//   - the URL refuses a different Content-Type, and stops working after UploadUrlSeconds
//   - completing before the upload is Missing, with the wrong size is SizeMismatch, and the file
//     stays pending either way; completing twice is NotPending
//
// The presigned URL must use http (MinIO here has no TLS): set Protocol = Protocol.HTTP on the
// request. A missing object makes GetObjectMetadataAsync throw AmazonS3Exception with
// StatusCode NotFound.
//
// Tests: dotnet test --filter-namespace "*Stage1"
public sealed class PresignedUploads(FileRepository files, Blobs blobs, IOptions<BlobsOptions> options, TimeProvider time)
{
    private readonly TimeSpan _urlLifetime = TimeSpan.FromSeconds(options.Value.UploadUrlSeconds);

    public Task<UploadTicket> CreateAsync(long ownerId, NewFile file, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<CompleteResult> CompleteAsync(long fileId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
