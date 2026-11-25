using System.Text.Json.Serialization;
using Contention;
using Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<SaleOptions>().Bind(builder.Configuration.GetSection(SaleOptions.Section));
var sale = builder.Configuration.GetSection(SaleOptions.Section).Get<SaleOptions>() ?? new SaleOptions();

// outcomes as names ("SoldOut"), not numbers
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Stats>();
builder.Services.AddSingleton<PurchaseCheck>();

var sales = builder.Configuration.GetConnectionString("Sales")
    ?? throw new InvalidOperationException("ConnectionStrings:Sales is not set");
// optional: empty means no payments database
var payments = builder.Configuration.GetConnectionString("Payments") is { Length: > 0 } p ? p : null;
// a factory, not an instance, so the container disposes the pools when the app stops
builder.Services.AddSingleton(_ => new Databases(
    NpgsqlDataSource.Create(sales),
    payments is null ? null : NpgsqlDataSource.Create(payments)));
builder.Services.AddSingleton<SeatRepository>();
builder.Services.AddSingleton<Audit>();
builder.Services.AddSingleton<NaiveSeatSeller>();

switch (sale.Tickets)
{
    case TicketStrategy.Naive:
        builder.Services.AddSingleton<ITicketSeller, NaiveTicketSeller>();
        break;
    case TicketStrategy.Conditional:
        builder.Services.AddSingleton<ITicketSeller, ConditionalTicketSeller>();
        break;
    case TicketStrategy.Locking:
        builder.Services.AddSingleton<ITicketSeller, LockingTicketSeller>();
        break;
    case TicketStrategy.TwoDatabases:
        if (payments is null)
        {
            throw new InvalidOperationException("Sale:Tickets is TwoDatabases but ConnectionStrings:Payments is not set");
        }
        builder.Services.AddSingleton<ITicketSeller, TwoDatabaseTicketSeller>();
        break;
}

switch (sale.Seats)
{
    case SeatStrategy.Naive:
        builder.Services.AddSingleton<IBestSeatSeller>(sp => sp.GetRequiredService<NaiveSeatSeller>());
        break;
    case SeatStrategy.Optimistic:
        builder.Services.AddSingleton<IBestSeatSeller, OptimisticSeatSeller>();
        break;
    case SeatStrategy.Held:
        var redis = builder.Configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("Sale:Seats is Held but ConnectionStrings:Redis is not set");
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
        builder.Services.AddSingleton<SeatHolds>();
        builder.Services.AddSingleton<IBestSeatSeller>(sp => sp.GetRequiredService<NaiveSeatSeller>());
        break;
}

var app = builder.Build();

// Connect to Redis now, not inside the first request.
app.Services.GetService<SeatHolds>();

if (app.Configuration.GetValue("Db:Migrate", true))
{
    var db = app.Services.GetRequiredService<Databases>();
    await Migrator.ApplyAsync(db.Sales, Migrator.SalesDirectory, app.Logger);
    if (db.Payments is not null)
    {
        await Migrator.ApplyAsync(db.Payments, Migrator.PaymentsDirectory, app.Logger);
    }
}

app.MapEndpoints();

app.Run();

public partial class Program;
