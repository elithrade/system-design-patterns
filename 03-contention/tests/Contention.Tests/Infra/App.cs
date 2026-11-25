using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Contention.Tests;

// The app under test, on its own cloned database(s), with a fake clock.
public sealed class App : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private App(Dictionary<string, string?> settings) => _settings = settings;

    public static App Create(
        InfraFixture infra,
        string sales,
        TicketStrategy tickets = TicketStrategy.Naive,
        SeatStrategy seats = SeatStrategy.Naive,
        string? payments = null,
        int checkMilliseconds = 2,
        Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sales"] = sales,
            ["ConnectionStrings:Payments"] = payments ?? "",
            ["ConnectionStrings:Redis"] = infra.RedisConnectionString,
            ["Sale:Tickets"] = tickets.ToString(),
            ["Sale:Seats"] = seats.ToString(),
            ["Sale:CheckMilliseconds"] = checkMilliseconds.ToString(),
            // a test never sees another test's keys
            ["Sale:KeyPrefix"] = $"t{Guid.NewGuid():N}:",
            // the fixture migrated the templates already
            ["Db:Migrate"] = "false",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }
        var app = new App(settings);
        // build the host now: a thousand concurrent first calls to CreateClient would each build one
        _ = app.Server;
        return app;
    }

    public Stats Stats => Services.GetRequiredService<Stats>();
    public PurchaseCheck Check => Services.GetRequiredService<PurchaseCheck>();

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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static HttpClient As(this App app, long buyerId)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(Endpoints.BuyerHeader, buyerId.ToString());
        return client;
    }

    public static Task<PurchaseResult> BuyTicketAsync(this HttpClient client, long saleId = 1) =>
        SendAsync(client, HttpMethod.Post, $"/sales/{saleId}/tickets");

    public static Task<PurchaseResult> BuyBestSeatAsync(this HttpClient client, long saleId = 1) =>
        SendAsync(client, HttpMethod.Post, $"/sales/{saleId}/seats/best");

    public static Task<PurchaseResult> HoldAsync(this HttpClient client, long seatId) =>
        SendAsync(client, HttpMethod.Post, $"/seats/{seatId}/hold");

    public static Task<PurchaseResult> PurchaseSeatAsync(this HttpClient client, long seatId) =>
        SendAsync(client, HttpMethod.Post, $"/seats/{seatId}/purchase");

    public static async Task<HttpStatusCode> ReleaseAsync(this HttpClient client, long seatId) =>
        (await client.DeleteAsync($"/seats/{seatId}/hold")).StatusCode;

    public static async Task<SaleAudit> AuditAsync(this HttpClient client, long saleId = 1) =>
        (await client.GetFromJsonAsync<SaleAudit>($"/sales/{saleId}"))!;

    public static async Task<Seat> SeatAsync(this HttpClient client, long seatId) =>
        (await client.GetFromJsonAsync<Seat>($"/seats/{seatId}"))!;

    private static async Task<PurchaseResult> SendAsync(HttpClient client, HttpMethod method, string url)
    {
        using var response = await client.SendAsync(new HttpRequestMessage(method, url));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is not HttpStatusCode.InternalServerError and not HttpStatusCode.NotImplemented,
            $"{method} {url}: {(int)response.StatusCode} {body}");
        var result = JsonSerializer.Deserialize<PurchaseResult>(body, Json)!;
        var expected = result.Outcome switch
        {
            Outcome.Bought => HttpStatusCode.Created,
            Outcome.Held => HttpStatusCode.OK,
            Outcome.Busy => HttpStatusCode.ServiceUnavailable,
            Outcome.NotFound => HttpStatusCode.NotFound,
            _ => HttpStatusCode.Conflict,
        };
        Assert.Equal(expected, response.StatusCode);
        return result;
    }
}
