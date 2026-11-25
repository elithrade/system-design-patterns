namespace VideoPlatform;

public enum Engine
{
    // the baseline: transcode inside the request that says the upload finished
    Inline,
    // stage 2: a job per rendition, run by workers
    Queue,
    // stage 4: a Temporal workflow per video
    Temporal,
}

public enum BusKind
{
    // in-process: progress reaches watchers on the same instance only
    Local,
    // stage 3: Postgres LISTEN/NOTIFY, across instances
    Postgres,
}

// The "Pipeline" section. Set from the command line with Pipeline__Engine=Queue etc.
public sealed class PipelineOptions
{
    public const string Section = "Pipeline";

    public Engine Engine { get; set; } = Engine.Inline;
    public BusKind Bus { get; set; } = BusKind.Local;
}

// The "S3" section: where MinIO is.
public sealed class S3Options
{
    public const string Section = "S3";

    public string ServiceUrl { get; set; } = "http://localhost:59100";
    public string AccessKey { get; set; } = "minio";
    public string SecretKey { get; set; } = "minio-secret";
}

// The "Storage" section.
public sealed class StorageOptions
{
    public const string Section = "Storage";

    // The bucket the app creates at startup. The tests give each test its own.
    public string Bucket { get; set; } = "videos";

    // Stage 1: how long a presigned upload URL works.
    public int UploadUrlSeconds { get; set; } = 900;

    // Stage 1: the largest upload POST /videos accepts.
    public int MaxUploadMb { get; set; } = 2048;
}

// The "Transcode" section.
public sealed class TranscodeOptions
{
    public const string Section = "Transcode";

    // The simulated transcoder spends this long per MB of source, times the rendition's cost.
    public int MsPerMb { get; set; } = 20;

    // The renditions every video gets, and what each costs relative to the source (720p: twice as
    // long as 360p).
    public Dictionary<string, double> Renditions { get; set; } = new() { ["360p"] = 1.0, ["720p"] = 2.0, ["thumb"] = 0.1 };

    // Stage 2: worker loops per instance, and whether to run them (the tests run jobs by hand).
    public int Workers { get; set; } = 4;
    public bool RunWorkers { get; set; } = true;
    public int PollMilliseconds { get; set; } = 500;

    // Stage 2: a claimed job belongs to its worker until the lease ends; progress extends it.
    public int LeaseSeconds { get; set; } = 30;

    // Stages 2 and 4: a failed attempt waits BackoffSeconds * 2^(attempt - 1) before the next;
    // after MaxAttempts the rendition, and the video, fail.
    public int BackoffSeconds { get; set; } = 2;
    public int MaxAttempts { get; set; } = 3;
}

// The "Live" section (stage 3).
public sealed class LiveOptions
{
    public const string Section = "Live";

    // A progress stream with nothing to say sends a comment line this often.
    public int HeartbeatSeconds { get; set; } = 15;
}

// The "Temporal" section (stage 4).
public sealed class TemporalOptions
{
    public const string Section = "Temporal";

    public string Address { get; set; } = "localhost:57333";
    public string Namespace { get; set; } = "default";
    public string TaskQueue { get; set; } = "publish";

    // Workflow ids are WorkflowIdPrefix + "video-" + the video id. The tests give each test its own,
    // because every test database numbers its videos from 1.
    public string WorkflowIdPrefix { get; set; } = "";

    // A video whose upload has not arrived this long after POST /videos is abandoned.
    public int UploadWaitSeconds { get; set; } = 3600;

    // A transcode activity that has not heartbeated for this long is presumed dead and retried.
    public int HeartbeatTimeoutSeconds { get; set; } = 10;
}
