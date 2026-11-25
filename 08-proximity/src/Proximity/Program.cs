using Dapper;
using Npgsql;
using Proximity;
using StackExchange.Redis;

// driver_id -> DriverId
DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ProximityOptions>().Bind(builder.Configuration.GetSection(ProximityOptions.Section));
var proximity = builder.Configuration.GetSection(ProximityOptions.Section).Get<ProximityOptions>() ?? new ProximityOptions();

builder.Services.AddSingleton(TimeProvider.System);
var rides = builder.Configuration.GetConnectionString("Rides")
    ?? throw new InvalidOperationException("ConnectionStrings:Rides is not set");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(rides));
builder.Services.AddSingleton<DriverStore>();

switch (proximity.Engine)
{
    case Engine.Geohash:
        builder.Services.AddSingleton<IDriverIndex, GeohashIndex>();
        break;
    case Engine.PostGis:
        builder.Services.AddSingleton<IDriverIndex, PostGisIndex>();
        break;
    case Engine.Redis:
        var redis = builder.Configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("ConnectionStrings:Redis is not set");
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
        builder.Services.AddSingleton<RedisDriverIndex>();
        builder.Services.AddSingleton<IDriverIndex>(sp => sp.GetRequiredService<RedisDriverIndex>());
        break;
    default:
        builder.Services.AddSingleton<IDriverIndex, ScanIndex>();
        break;
}

var app = builder.Build();

if (app.Configuration.GetValue("Db:Migrate", true))
{
    await Migrator.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);
}

app.MapEndpoints();

app.Run();

public partial class Program;
