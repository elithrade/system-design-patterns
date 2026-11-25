using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace ScalingWrites.Tests;

// The app under test, on its own cloned database(s), with a fake clock.
public sealed class App : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private App(Dictionary<string, string?> settings) => _settings = settings;

    // main: a clone of the seeded main database. partitions: clones of the empty partition, for
    // the Partitioned counter. extra: any other Writes:* setting, e.g. ["Writes:Shards"] = "8".
    public static App Create(
        string main,
        CounterKind counter = CounterKind.Direct,
        IReadOnlyList<string>? partitions = null,
        Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Main"] = main,
            ["Writes:Counter"] = counter.ToString(),
            // the fixture migrated the templates already
            ["Db:Migrate"] = "false",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        for (var i = 0; i < (partitions?.Count ?? 0); i++)
        {
            settings[$"ConnectionStrings:Partition{i}"] = partitions![i];
        }
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }
        return new App(settings);
    }

    public Stats Stats => Services.GetRequiredService<Stats>();
    public Faults Faults => Services.GetRequiredService<Faults>();
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

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

public sealed record VideoResponse(long Id, string Title, long Views);

public static class ClientExtensions
{
    public static async Task<HttpStatusCode> ViewAsync(this HttpClient client, long videoId)
    {
        using var response = await client.PostAsync($"/videos/{videoId}/views", null);
        return response.StatusCode;
    }

    public static async Task<long> ViewsAsync(this HttpClient client, long videoId)
    {
        var response = await client.GetAsync($"/videos/{videoId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VideoResponse>())!.Views;
    }

    public static async Task<List<VideoViews>> TopAsync(this HttpClient client, int limit)
    {
        var response = await client.GetAsync($"/videos/top?limit={limit}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<VideoViews>>())!;
    }
}

public static class Eventually
{
    // Polls until check passes or the timeout runs out, then runs it once more so its assertion
    // failure is the one reported.
    public static async Task AssertAsync(Func<Task> check, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            try
            {
                await check();
                return;
            }
            catch (Exception)
            {
                await Task.Delay(50);
            }
        }
        await check();
    }
}
