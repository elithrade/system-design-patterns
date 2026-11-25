using Amazon.S3;
using Microsoft.Extensions.Options;

namespace VideoPlatform;

// What POST /videos sends: the video the client is about to upload.
public sealed record NewVideo(string Title, string ContentType, long Size);

// What POST /videos answers: where to PUT the bytes, and until when.
public sealed record UploadTicket(long Id, string UploadUrl, DateTimeOffset ExpiresAt);

public enum ConfirmResult
{
    // the source is in MinIO with the declared size; the video is now uploaded
    Confirmed,
    // the video was already confirmed (uploaded, processing or published)
    AlreadyConfirmed,
    // no such video
    NotFound,
    // no object in MinIO yet; the video stays pending
    NotUploaded,
    // the object's size is not the declared size; the video stays pending
    SizeMismatch,
    // the video failed or was abandoned
    Closed,
}

// Stage 1: lab 4's presigned upload, for videos.
//
// CreateAsync: refuse (ArgumentException, which the endpoint turns into a 400) a content type
// that does not start with "video/", or a size that is not between 1 byte and MaxUploadMb.
// Otherwise insert the video (VideoStore.CreateAsync) and presign a PUT for Blobs.SourceKey that
// works for UploadUrlSeconds and only with the declared content type. Use http (Protocol.HTTP).
//
// ConfirmAsync: the client says the upload finished. Check MinIO (Blobs.SizeAsync) and move the
// video from pending to uploaded (VideoStore.SetStatusAsync) if the object is there with the
// declared size. Two confirms can arrive at once (a client retrying a slow request): exactly one
// may be Confirmed.
//
// The endpoint starts processing after Confirmed AND after AlreadyConfirmed: a client that
// retries because the first answer was lost must not leave the video stuck at uploaded. That
// makes every engine's "start processing" safe to call twice.
//
// Tests: dotnet test --filter-namespace "*Stage1"
public sealed class VideoUploads(VideoStore videos, Blobs blobs, IOptions<StorageOptions> options, TimeProvider time)
{
    public Task<UploadTicket> CreateAsync(long ownerId, NewVideo video, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<ConfirmResult> ConfirmAsync(long videoId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
