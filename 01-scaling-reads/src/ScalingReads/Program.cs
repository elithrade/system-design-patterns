using Npgsql;
using ScalingReads;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ReadsOptions>().Bind(builder.Configuration.GetSection(ReadsOptions.Section));
var reads = builder.Configuration.GetSection(ReadsOptions.Section).Get<ReadsOptions>() ?? new ReadsOptions();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Stats>();

// Two pools. Without a replica configured, "replica" reads go to the primary.
var primary = builder.Configuration.GetConnectionString("Primary")
    ?? throw new InvalidOperationException("ConnectionStrings:Primary is not set");
var replica = builder.Configuration.GetConnectionString("Replica") ?? primary;
builder.Services.AddSingleton(new DataSources(
    NpgsqlDataSource.Create(primary),
    NpgsqlDataSource.Create(replica)));
builder.Services.AddSingleton<PostRepository>();

// Stage 2: which database serves a read.
switch (reads.Router)
{
    case RouterKind.Primary:
        builder.Services.AddSingleton<IConnectionRouter, PrimaryOnlyRouter>();
        break;
    case RouterKind.Replica:
        builder.Services.AddSingleton<IConnectionRouter, ReplicaOnlyRouter>();
        break;
    case RouterKind.ReadYourWrites:
        builder.Services.AddSingleton<IConnectionRouter, ReadYourWritesRouter>();
        break;
}

// Stages 3 and 4: what sits between GET /posts/{id} and the database.
if (reads.Cache == CacheKind.None)
{
    builder.Services.AddSingleton<IPostReader>(sp => sp.GetRequiredService<PostRepository>());
    builder.Services.AddSingleton<IPostChangeListener, NoOpChangeListener>();
}
else
{
    var redis = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException($"Reads:Cache is {reads.Cache} but ConnectionStrings:Redis is not set");
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
    builder.Services.AddSingleton<ICache, RedisCache>();
    builder.Services.AddSingleton<CachedPostReader>();
    builder.Services.AddSingleton<IPostChangeListener>(sp => sp.GetRequiredService<CachedPostReader>());
    if (reads.Cache == CacheKind.Redis)
    {
        builder.Services.AddSingleton<IPostReader>(sp => sp.GetRequiredService<CachedPostReader>());
    }
    else
    {
        builder.Services.AddSingleton<HotKeyPostReader>();
        builder.Services.AddSingleton<IPostReader>(sp => sp.GetRequiredService<HotKeyPostReader>());
    }
}

var app = builder.Build();

// Build the reader chain (and connect to Redis) now, not inside the first request: a burst of first
// requests would all block on the connect and starve the thread pool.
app.Services.GetRequiredService<IPostReader>();
app.Services.GetRequiredService<IConnectionRouter>();

if (app.Configuration.GetValue("Db:Migrate", true))
{
    await Migrator.ApplyAsync(app.Services.GetRequiredService<DataSources>().Primary, app.Logger);
}

app.MapEndpoints();

app.Run();

public partial class Program;
