using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace LongRunning.Tests;

// The app under test, on its own cloned database, with a fake clock and no worker loops: tests run
// jobs by hand with Runner.RunOnceAsync or call the queue directly.
public sealed class App : WebApplicationFactory<Program>
{
    static App() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private readonly Dictionary<string, string?> _settings;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2027, 1, 2, 9, 0, 0, TimeSpan.Zero));
    public string Db { get; }

    private App(Dictionary<string, string?> settings, string db)
    {
        _settings = settings;
        Db = db;
    }

    public static async Task<App> StartAsync(InfraFixture infra, QueueKind queue, Dictionary<string, string?>? extra = null)
    {
        var db = await infra.CloneAsync();
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Stride"] = db,
            ["Jobs:Queue"] = queue.ToString(),
            ["Jobs:Workers"] = "0",
            ["Jobs:LeaseSeconds"] = "30",
            ["Jobs:BackoffSeconds"] = "2",
            ["Jobs:MaxAttempts"] = "10",
            ["Reports:RenderMilliseconds"] = "20",
            ["Reports:SummaryMilliseconds"] = "5",
            // the fixture migrated the template already
            ["Db:Migrate"] = "false",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }
        var app = new App(settings, db);
        _ = app.Server;
        return app;
    }

    public DateTimeOffset Now => Time.GetUtcNow();
    public Stats Stats => Services.GetRequiredService<Stats>();
    public Faults Faults => Services.GetRequiredService<Faults>();
    public JobRunner Runner => Services.GetRequiredService<JobRunner>();
    public IJobQueue Queue => Services.GetRequiredService<IJobQueue>();
    public SkipLockedQueue SkipLocked => Services.GetRequiredService<SkipLockedQueue>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // not Development: no developer exception page, so 500 failing requests stay cheap
        builder.UseEnvironment("Test");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time)));
    }
}

public static class ClientExtensions
{
    public static async Task<JobView> EnqueueReportAsync(this HttpClient client, long userId, int year = 2026)
    {
        var response = await client.PostAsync($"/users/{userId}/year-in-review?year={year}", null);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"POST: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var job = (await response.Content.ReadFromJsonAsync<JobView>())!;
        Assert.Equal($"/jobs/{job.Id}", response.Headers.Location?.ToString());
        return job;
    }

    public static async Task<JobView> JobAsync(this HttpClient client, long id) =>
        (await client.GetFromJsonAsync<JobView>($"/jobs/{id}"))!;
}
