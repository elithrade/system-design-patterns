using Microsoft.Extensions.Options;
using Npgsql;
using ScalingWrites;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<WritesOptions>().Bind(builder.Configuration.GetSection(WritesOptions.Section));
var writes = builder.Configuration.GetSection(WritesOptions.Section).Get<WritesOptions>() ?? new WritesOptions();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Stats>();
builder.Services.AddSingleton<Faults>();

var main = builder.Configuration.GetConnectionString("Main")
    ?? throw new InvalidOperationException("ConnectionStrings:Main is not set");
var partitions = Enumerable.Range(0, Stats.MaxPartitions)
    .Select(i => builder.Configuration.GetConnectionString($"Partition{i}"))
    .OfType<string>()
    .ToList();
// a factory, not an instance, so the container disposes the pools when the app stops
builder.Services.AddSingleton(_ => new Databases(
    NpgsqlDataSource.Create(main),
    partitions.Select(NpgsqlDataSource.Create).ToList()));
builder.Services.AddSingleton<VideoRepository>();
builder.Services.AddSingleton<PartitionRepository>();

switch (writes.Counter)
{
    case CounterKind.Direct:
        builder.Services.AddSingleton<IViewCounter, DirectViewCounter>();
        builder.Services.AddSingleton<ITopVideos, DatabaseTopVideos>();
        break;

    case CounterKind.Batched:
        builder.Services.AddSingleton<BatchingViewCounter>();
        builder.Services.AddSingleton<IViewCounter>(sp => sp.GetRequiredService<BatchingViewCounter>());
        builder.Services.AddSingleton<ITopVideos, DatabaseTopVideos>();
        builder.Services.AddHostedService<FlushLoop>();
        break;

    case CounterKind.Sharded:
        builder.Services.AddSingleton<IViewCounter, ShardedViewCounter>();
        break;

    case CounterKind.Partitioned:
        if (partitions.Count == 0)
        {
            throw new InvalidOperationException("Writes:Counter is Partitioned but ConnectionStrings:Partition0.. are not set");
        }
        builder.Services.AddSingleton<IPartitioner>(sp => writes.Partitioner == PartitionerKind.Ring
            ? ActivatorUtilities.CreateInstance<RingPartitioner>(sp)
            : ActivatorUtilities.CreateInstance<ModuloPartitioner>(sp));
        builder.Services.AddSingleton<IViewCounter, PartitionedViewCounter>();
        builder.Services.AddSingleton<ITopVideos, ScatterGatherTopVideos>();
        break;

    case CounterKind.Queued:
        builder.Services.AddSingleton<ViewQueue>();
        builder.Services.AddSingleton<IViewCounter, QueuedViewCounter>();
        builder.Services.AddSingleton<ITopVideos, DatabaseTopVideos>();
        if (writes.Drainer)
        {
            builder.Services.AddHostedService<QueueDrainer>();
        }
        break;
}

var app = builder.Build();

if (writes.Counter == CounterKind.Queued)
{
    var queue = app.Services.GetRequiredService<ViewQueue>();
    app.Services.GetRequiredService<Stats>().QueueDepthProbe = () => queue.Depth;
}

if (app.Configuration.GetValue("Db:Migrate", true))
{
    var db = app.Services.GetRequiredService<Databases>();
    await Migrator.ApplyAsync(db.Main, Migrator.MainDirectory, app.Logger);
    foreach (var partition in db.Partitions)
    {
        await Migrator.ApplyAsync(partition, Migrator.PartitionDirectory, app.Logger);
    }
}

// Build the counter now, so a ring is built before the first request.
app.Services.GetRequiredService<IViewCounter>();

app.MapEndpoints();

app.Run();

public partial class Program;
