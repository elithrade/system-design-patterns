using Microsoft.Extensions.Options;

namespace LargeBlobs;

public enum DownloadOutcome
{
    // Url is a presigned GET; the endpoint redirects to it (302)
    Ok,
    // 404
    NotFound,
    // the file is not ready: 409
    NotReady,
    // the requester does not own the file: 403
    Forbidden,
}

public sealed record DownloadResult(DownloadOutcome Outcome, string? Url = null, DateTimeOffset? ExpiresAt = null);

// Stage 5. Downloads skip the API too: GET /files/{id}/download checks who is asking, then redirects
// to a presigned GET that works for DownloadSeconds. Anyone holding the URL can use it until then,
// so keep it short.
//
// The goal, which the tests check:
//   - the owner of a ready file gets a URL that returns exactly the file's bytes, with
//     Content-Disposition: attachment; filename="<the file's name>", so a browser saves it under
//     its name instead of showing it
//   - anyone else gets Forbidden; a pending file is NotReady; no row is NotFound
//   - the URL stops working after DownloadSeconds
//   - the API reads none of the bytes
//
// GetPreSignedUrlRequest has ResponseHeaderOverrides. Use Protocol.HTTP, as in stage 1.
//
// Tests: dotnet test --filter-namespace "*Stage5"
public sealed class DownloadLinks(FileRepository files, Blobs blobs, IOptions<BlobsOptions> options, TimeProvider time)
{
    private readonly TimeSpan _lifetime = TimeSpan.FromSeconds(options.Value.DownloadSeconds);

    public Task<DownloadResult> CreateAsync(long fileId, long requesterId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
