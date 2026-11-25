using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Realtime.Tests;

// The app under test, with a fake clock. Two Apps can share a database and a Redis prefix to play
// two instances behind a load balancer (stage 4).
public sealed class App : WebApplicationFactory<Program>
{
    public const int HeartbeatSeconds = 15;
    public const int MaxWaitSeconds = 30;

    private readonly Dictionary<string, string?> _settings;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
    public string Db { get; }
    public string Prefix { get; }

    private App(Dictionary<string, string?> settings, string db, string prefix)
    {
        _settings = settings;
        Db = db;
        Prefix = prefix;
    }

    // On Kestrel, a real socket server (stage 3): the in-memory test server is more forgiving about
    // how sockets are used than Kestrel is.
    public bool OnKestrel { get; private set; }

    public Uri KestrelAddress => new(Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
        .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First());

    public static async Task<App> StartAsync(InfraFixture infra, BusKind bus = BusKind.Local, App? sameBackendAs = null,
        Dictionary<string, string?>? extra = null, bool kestrel = false)
    {
        var db = sameBackendAs?.Db ?? await infra.CloneAsync();
        var prefix = sameBackendAs?.Prefix ?? $"t{Guid.NewGuid():N}:";
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Auctions"] = db,
            ["ConnectionStrings:Redis"] = infra.RedisConnectionString,
            ["Realtime:Bus"] = bus.ToString(),
            ["Realtime:ChannelPrefix"] = prefix,
            ["Realtime:HeartbeatSeconds"] = HeartbeatSeconds.ToString(),
            ["Realtime:MaxWaitSeconds"] = MaxWaitSeconds.ToString(),
            ["Realtime:PageSize"] = "100",
            // the fixture migrated the template already
            ["Db:Migrate"] = "false",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }
        var app = new App(settings, db, prefix);
        if (kestrel)
        {
            app.OnKestrel = true;
            app.UseKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
            app.StartServer();
        }
        else
        {
            _ = app.Server;
        }
        return app;
    }

    public Stats Stats => Services.GetRequiredService<Stats>();
    public ReadHooks Hooks => Services.GetRequiredService<ReadHooks>();
    public IBidBus Bus => Services.GetRequiredService<IBidBus>();
    public BidService Bids => Services.GetRequiredService<BidService>();

    // Places a bid through the service (as a hook must: it runs inside a request).
    public async Task<BidEvent> BidAsync(long auctionId, long amount, long bidderId = 1)
    {
        var result = await Bids.PlaceAsync(auctionId, bidderId, amount, CancellationToken.None);
        Assert.True(result.Outcome == BidOutcome.Accepted, $"bid {amount} on {auctionId}: {result.Outcome}");
        return result.Bid!;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // not Development: no developer exception page
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
    public static async Task<BidEvent> PlaceBidAsync(this HttpClient client, long auctionId, long amount, long bidderId = 1)
    {
        var response = await client.PostAsJsonAsync($"/auctions/{auctionId}/bids", new PlaceBid(bidderId, amount));
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"bid {amount}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<BidEvent>(Json.Options))!;
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

    public static Task AssertAsync(Action check, int timeoutMs = 5000) =>
        AssertAsync(() =>
        {
            check();
            return Task.CompletedTask;
        }, timeoutMs);
}
