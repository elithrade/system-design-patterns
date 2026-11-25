namespace LargeBlobs;

// Stage 3. The client's "complete" call is a promise it may not keep: it can crash, lose its
// connection, or never call. MinIO knows when an object is created and POSTs an event to
// /events/minio (compose.yaml sets the target up). Use it to mark files ready.
//
// HandleAsync gets one event, which may hold several records. For each record:
//   - only s3:ObjectCreated:* events in this app's bucket (blobs.Bucket) count; ignore the rest
//   - the key is URL-encoded; Blobs.IdFromKey turns "files/12" into 12; ignore keys that are not ours
//   - a pending file whose declared size equals the object's size becomes ready; a different
//     size makes it failed
//   - files that are not pending, or have no row, are left alone
// Return how many files changed status.
//
// The goal, which the tests check:
//   - after a presigned upload, the event alone makes the file ready, no complete call needed
//   - the same event delivered twice changes nothing the second time (MinIO retries deliveries)
//   - a size mismatch marks the file failed
//   - other event types, other buckets, and keys that are not ours change nothing
//
// Tests: dotnet test --filter-namespace "*Stage3"
public sealed class ObjectCreatedHandler(FileRepository files, Blobs blobs)
{
    public Task<int> HandleAsync(S3Event e, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
