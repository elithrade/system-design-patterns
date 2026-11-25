using Amazon.Runtime;
using Amazon.S3;
using LargeBlobs;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<BlobsOptions>().Bind(builder.Configuration.GetSection(BlobsOptions.Section));
var blobs = builder.Configuration.GetSection(BlobsOptions.Section).Get<BlobsOptions>() ?? new BlobsOptions();
var s3 = builder.Configuration.GetSection(S3Options.Section).Get<S3Options>() ?? new S3Options();

// The baseline accepts any body size, as a naive upload endpoint does (Kestrel's default is 30 MB).
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Stats>();

var files = builder.Configuration.GetConnectionString("Files")
    ?? throw new InvalidOperationException("ConnectionStrings:Files is not set");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(files));
builder.Services.AddSingleton<FileRepository>();

builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
    new BasicAWSCredentials(s3.AccessKey, s3.SecretKey),
    new AmazonS3Config
    {
        ServiceURL = s3.ServiceUrl,
        // MinIO serves buckets at /bucket, not at bucket.host
        ForcePathStyle = true,
        AuthenticationRegion = "us-east-1",
        // only send and check the newer checksums when an operation requires them
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    }));
builder.Services.AddSingleton<Blobs>();

builder.Services.AddSingleton<PresignedUploads>();
builder.Services.AddSingleton<MultipartUploads>();
builder.Services.AddSingleton<ObjectCreatedHandler>();
builder.Services.AddSingleton<OrphanSweeper>();
builder.Services.AddSingleton<DownloadLinks>();
if (blobs.Sweeper)
{
    builder.Services.AddHostedService<SweepLoop>();
}

var app = builder.Build();

if (app.Configuration.GetValue("Db:Migrate", true))
{
    await Migrator.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);
}
await app.Services.GetRequiredService<Blobs>().EnsureBucketAsync(blobs.Notifications, CancellationToken.None);

app.MapEndpoints();

app.Run();

public partial class Program;
