using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using Realtime;
using StackExchange.Redis;

// auction_id -> AuctionId
DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RealtimeOptions>().Bind(builder.Configuration.GetSection(RealtimeOptions.Section));
var realtime = builder.Configuration.GetSection(RealtimeOptions.Section).Get<RealtimeOptions>() ?? new RealtimeOptions();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Stats>();
builder.Services.AddSingleton<ReadHooks>();

var auctionsDb = builder.Configuration.GetConnectionString("Auctions")
    ?? throw new InvalidOperationException("ConnectionStrings:Auctions is not set");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(auctionsDb));
builder.Services.AddSingleton<Auctions>();
builder.Services.AddSingleton<BidService>();

if (realtime.Bus == BusKind.Redis)
{
    var redis = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is not set");
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
    builder.Services.AddSingleton<IBidBus, RedisBidBus>();
}
else
{
    builder.Services.AddSingleton<IBidBus, LocalBidBus>();
}

builder.Services.AddSingleton<LongPoll>();
builder.Services.AddSingleton<BidStream>();
builder.Services.AddSingleton<BidSocket>();

var app = builder.Build();

if (app.Configuration.GetValue("Db:Migrate", true))
{
    await Migrator.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);
}

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
app.MapEndpoints();

app.Run();

public partial class Program;
