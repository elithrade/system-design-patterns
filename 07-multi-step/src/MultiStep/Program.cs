using Dapper;
using MultiStep;
using Npgsql;
using Temporalio.Client;

// order_id -> OrderId
DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<CheckoutOptions>().Bind(builder.Configuration.GetSection(CheckoutOptions.Section));
builder.Services.AddOptions<FaultOptions>().Bind(builder.Configuration.GetSection(FaultOptions.Section));
builder.Services.AddOptions<TemporalOptions>().Bind(builder.Configuration.GetSection(TemporalOptions.Section));
var checkout = builder.Configuration.GetSection(CheckoutOptions.Section).Get<CheckoutOptions>() ?? new CheckoutOptions();
var temporal = builder.Configuration.GetSection(TemporalOptions.Section).Get<TemporalOptions>() ?? new TemporalOptions();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Faults>();

var shop = builder.Configuration.GetConnectionString("Shop")
    ?? throw new InvalidOperationException("ConnectionStrings:Shop is not set");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(shop));
builder.Services.AddSingleton<Payments>();
builder.Services.AddSingleton<Inventory>();
builder.Services.AddSingleton<Shipping>();
builder.Services.AddSingleton<Orders>();
builder.Services.AddSingleton<Audit>();
builder.Services.AddSingleton<IdempotencyStore>();

builder.Services.AddSingleton<NaiveCheckout>();
builder.Services.AddSingleton<SagaCheckout>();
builder.Services.AddSingleton<SagaRecovery>();
switch (checkout.Engine)
{
    case Engine.Saga:
        builder.Services.AddSingleton<ICheckout>(sp => sp.GetRequiredService<SagaCheckout>());
        builder.Services.AddHostedService<RecoveryLoop>();
        break;
    case Engine.Temporal:
        // a worker needs a connected client; this fails at startup if Temporal is not running
        var temporalClient = await TemporalClient.ConnectAsync(new(temporal.Address) { Namespace = temporal.Namespace });
        builder.Services.AddSingleton<ITemporalClient>(temporalClient);
        builder.Services.AddSingleton<CheckoutActivities>();
        builder.Services.AddSingleton<ICheckout, TemporalCheckout>();
        builder.Services.AddHostedService<TemporalWorkerService>();
        break;
    default:
        builder.Services.AddSingleton<ICheckout>(sp => sp.GetRequiredService<NaiveCheckout>());
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
