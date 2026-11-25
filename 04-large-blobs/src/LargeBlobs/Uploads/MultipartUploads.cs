using Microsoft.Extensions.Options;

namespace LargeBlobs;

public sealed record PartUrl(int PartNumber, long Size, string Url);

// What POST /files/{id}/multipart answers: one presigned URL per part.
public sealed record MultipartPlan(long FileId, string UploadId, long PartSize, IReadOnlyList<PartUrl> Parts);

// A part MinIO already has. GET /files/{id}/multipart/parts lists them, so a client that lost its
// connection uploads only what is missing.
public sealed record UploadedPart(int PartNumber, long Size, string ETag);

// Stage 2. Big files go up in parts: each part has its own presigned URL, parts can go in
// parallel, a failed part is retried alone, and a client that crashes resumes where it stopped.
//
// StartAsync: for a pending file (created by POST /files), start a multipart upload in MinIO,
// remember its upload id on the row (files.SetUploadIdAsync), and presign a PUT for every part:
// PartSizeMb each, the last one smaller. Calling it again for the same file returns a plan for
// the same upload id. Null if there is no such pending file.
// ListPartsAsync: the parts MinIO has for the file's upload, in part order. Null if the file has no
// upload in progress.
// CompleteAsync: if MinIO has every part, complete the upload and mark the file ready. If a part is
// missing, answer Missing and leave the upload open so the client can finish it.
// AbortAsync: abort the upload in MinIO and delete the row. False if there was none.
//
// The goal, which the tests check:
//   - a 12 MiB file with 5 MiB parts gets 3 URLs (5, 5, 2 MiB); after PUTting all three and
//     completing, the object is the three parts in order and the file is ready
//   - after uploading parts 1 and 3, ListPartsAsync says 1 and 3; uploading 2 and completing works
//   - completing with a part missing is Missing, and the upload is still open
//   - after AbortAsync, MinIO lists no upload for the key and the file is gone
//   - the API reads none of the bytes
//
// GetPreSignedUrlRequest takes UploadId and PartNumber. CompleteMultipartUploadRequest needs every
// part's ETag: ask MinIO for them (ListPartsAsync) rather than trusting the client.
//
// Tests: dotnet test --filter-namespace "*Stage2"
public sealed class MultipartUploads(FileRepository files, Blobs blobs, IOptions<BlobsOptions> options, TimeProvider time)
{
    private readonly long _partSize = options.Value.PartSizeMb * 1024L * 1024L;
    private readonly TimeSpan _urlLifetime = TimeSpan.FromSeconds(options.Value.UploadUrlSeconds);

    public Task<MultipartPlan?> StartAsync(long fileId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<IReadOnlyList<UploadedPart>?> ListPartsAsync(long fileId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<CompleteResult> CompleteAsync(long fileId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<bool> AbortAsync(long fileId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
