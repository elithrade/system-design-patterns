using Amazon.Runtime;
using Amazon.S3;
using Dapper;
using Npgsql;
using Temporalio.Client;
using VideoPlatform;

// owner_id -> OwnerId
DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<PipelineOptions>().Bind(builder.Configuration.GetSection(PipelineOptions.Section));
builder.Services.AddOptions<StorageOptions>().Bind(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.AddOptions<TranscodeOptions>().Bind(builder.Configuration.GetSection(TranscodeOptions.Section));
builder.Services.AddOptions<LiveOptions>().Bind(builder.Configuration.GetSection(LiveOptions.Section));
builder.Services.AddOptions<TemporalOptions>().Bind(builder.Configuration.GetSection(TemporalOptions.Section));
var pipeline = builder.Configuration.GetSection(PipelineOptions.Section).Get<PipelineOptions>() ?? new PipelineOptions();
var transcode = builder.Configuration.GetSection(TranscodeOptions.Section).Get<TranscodeOptions>() ?? new TranscodeOptions();
var temporal = builder.Configuration.GetSection(TemporalOptions.Section).Get<TemporalOptions>() ?? new TemporalOptions();
var s3 = builder.Configuration.GetSection(S3Options.Section).Get<S3Options>() ?? new S3Options();

// The baseline accepts any body size, as a naive upload endpoint does (Kestrel's default is 30 MB).
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Stats>();
builder.Services.AddSingleton<Faults>();
builder.Services.AddSingleton<ReadHooks>();

var videosDb = builder.Configuration.GetConnectionString("Videos")
    ?? throw new InvalidOperationException("ConnectionStrings:Videos is not set");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(videosDb));
builder.Services.AddSingleton<VideoStore>();

builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
    new BasicAWSCredentials(s3.AccessKey, s3.SecretKey),
    new AmazonS3Config
    {
        ServiceURL = s3.ServiceUrl,
        // MinIO serves buckets at /bucket, not at bucket.host
        ForcePathStyle = true,
        AuthenticationRegion = "us-east-1",
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    }));
builder.Services.AddSingleton<Blobs>();
builder.Services.AddSingleton<Transcoder>();

if (pipeline.Bus == BusKind.Postgres)
{
    builder.Services.AddSingleton<IProgressBus, PgProgressBus>();
}
else
{
    builder.Services.AddSingleton<IProgressBus, LocalProgressBus>();
}
builder.Services.AddSingleton<Progress>();
builder.Services.AddSingleton<ProgressStream>();

builder.Services.AddSingleton<VideoUploads>();
builder.Services.AddSingleton<InlinePipeline>();
builder.Services.AddSingleton<TranscodeQueue>();
builder.Services.AddSingleton<TranscodeRunner>();
switch (pipeline.Engine)
{
    case Engine.Queue:
        builder.Services.AddSingleton<IPipeline, QueuePipeline>();
        if (transcode.RunWorkers)
        {
            builder.Services.AddHostedService<TranscodeWorkers>();
        }
        break;
    case Engine.Temporal:
        // a worker needs a connected client; this fails at startup if Temporal is not running
        var temporalClient = await TemporalClient.ConnectAsync(new(temporal.Address) { Namespace = temporal.Namespace });
        builder.Services.AddSingleton<ITemporalClient>(temporalClient);
        builder.Services.AddSingleton<PublishActivities>();
        builder.Services.AddSingleton<IPipeline, TemporalPipeline>();
        builder.Services.AddHostedService<TemporalWorkerService>();
        break;
    default:
        builder.Services.AddSingleton<IPipeline>(sp => sp.GetRequiredService<InlinePipeline>());
        break;
}

var app = builder.Build();

if (app.Configuration.GetValue("Db:Migrate", true))
{
    await Migrator.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);
}
await app.Services.GetRequiredService<Blobs>().EnsureBucketAsync(CancellationToken.None);

app.MapEndpoints();

app.Run();

public partial class Program;
