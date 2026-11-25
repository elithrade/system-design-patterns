namespace LargeBlobs;

// The body MinIO POSTs for a bucket event (the S3 event notification format), trimmed to the fields
// this lab needs. A real one:
//
//   {"EventName": "s3:ObjectCreated:Put", "Key": "uploads/files/12",
//    "Records": [{"eventName": "s3:ObjectCreated:Put",
//                 "s3": {"bucket": {"name": "uploads"},
//                        "object": {"key": "files%2F12", "size": 1048576, "eTag": "..."}}}]}
//
// object.key is URL-encoded. One POST can carry several records.
public sealed record S3Event(IReadOnlyList<S3EventRecord>? Records);

public sealed record S3EventRecord(string EventName, S3Entity S3);

public sealed record S3Entity(S3BucketEntity Bucket, S3ObjectEntity Object);

public sealed record S3BucketEntity(string Name);

public sealed record S3ObjectEntity(string Key, long Size, string? ETag);
