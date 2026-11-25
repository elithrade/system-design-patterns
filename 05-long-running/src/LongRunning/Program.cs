using Dapper;
using LongRunning;
using Npgsql;

// user_id -> UserId
DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ReportsOptions>().Bind(builder.Configuration.GetSection(ReportsOptions.Section));
builder.Services.AddOptions<JobsOptions>().Bind(builder.Configuration.GetSection(JobsOptions.Section));
var jobs = builder.Configuration.GetSection(JobsOptions.Section).Get<JobsOptions>() ?? new JobsOptions();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Stats>();
builder.Services.AddSingleton<Faults>();

var stride = builder.Configuration.GetConnectionString("Stride")
    ?? throw new InvalidOperationException("ConnectionStrings:Stride is not set");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(stride));
builder.Services.AddSingleton<Renderer>();
builder.Services.AddSingleton<JobStore>();

if (jobs.Queue == QueueKind.SkipLocked)
{
    builder.Services.AddSingleton<SkipLockedQueue>();
    builder.Services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<SkipLockedQueue>());
}
else
{
    builder.Services.AddSingleton<IJobQueue, NaiveQueue>();
}
builder.Services.AddSingleton<JobRunner>();
if (jobs.Workers > 0)
{
    builder.Services.AddHostedService<Workers>();
}

var app = builder.Build();

if (app.Configuration.GetValue("Db:Migrate", true))
{
    await Migrator.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);
}

app.MapEndpoints();

app.Run();

public partial class Program;
