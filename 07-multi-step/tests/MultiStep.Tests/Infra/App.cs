using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MultiStep.Tests;

// The app under test, on its own cloned database, with a fake clock. Retries wait 1 ms.
public sealed class App : WebApplicationFactory<Program>
{
    public const int MaxAttempts = 5;
    public const int LeaseSeconds = 30;
    public const int KeyLockSeconds = 60;

    static App() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private readonly Dictionary<string, string?> _settings;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
    public string Db { get; }

    private App(Dictionary<string, string?> settings, string db)
    {
        _settings = settings;
        Db = db;
    }

    public static async Task<App> StartAsync(InfraFixture infra, Engine engine, Dictionary<string, string?>? extra = null, string? db = null)
    {
        db ??= await infra.CloneAsync();
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shop"] = db,
            ["Checkout:Engine"] = engine.ToString(),
            ["Checkout:MaxAttempts"] = MaxAttempts.ToString(),
            ["Checkout:RetryMilliseconds"] = "1",
            ["Checkout:LeaseSeconds"] = LeaseSeconds.ToString(),
            ["Checkout:RecoverySeconds"] = "0",
            ["Checkout:KeyLockSeconds"] = KeyLockSeconds.ToString(),
            ["Faults:TransientRate"] = "0",
            ["Faults:LostResponseRate"] = "0",
            ["Faults:LatencyMs"] = "0",
            // the fixture migrated the template already
            ["Db:Migrate"] = "false",
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Temporalio"] = "Error",
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }
        var app = new App(settings, db);
        _ = app.Server;
        return app;
    }

    public Faults Faults => Services.GetRequiredService<Faults>();
    public ICheckout Checkout => Services.GetRequiredService<ICheckout>();
    public SagaCheckout Saga => Services.GetRequiredService<SagaCheckout>();
    public SagaRecovery Recovery => Services.GetRequiredService<SagaRecovery>();
    public Orders Orders => Services.GetRequiredService<Orders>();

    public Task<AuditReport> AuditAsync() => Services.GetRequiredService<Audit>().ReportAsync(CancellationToken.None);

    public async Task<int> AvailableAsync(string sku)
    {
        await using var conn = new NpgsqlConnection(Db);
        return await conn.ExecuteScalarAsync<int>("SELECT available FROM products WHERE sku = @sku", new { sku });
    }

    // What the three services hold for one order.
    public async Task<Ledger> LedgerAsync(long orderId)
    {
        await using var conn = new NpgsqlConnection(Db);
        return await conn.QuerySingleAsync<Ledger>("""
            SELECT o.status, o.reason,
              (SELECT count(*) FROM payments WHERE order_id = o.id AND status = 'charged')::int AS charged,
              (SELECT count(*) FROM payments WHERE order_id = o.id AND status = 'refunded')::int AS refunded,
              (SELECT count(*) FROM reservations WHERE order_id = o.id AND status = 'held')::int AS held,
              (SELECT count(*) FROM reservations WHERE order_id = o.id AND status = 'released')::int AS released,
              (SELECT count(*) FROM shipments WHERE order_id = o.id)::int AS shipments
            FROM orders o WHERE o.id = @orderId
            """, new { orderId });
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

public sealed record Ledger(string Status, string? Reason, int Charged, int Refunded, int Held, int Released, int Shipments)
{
    public static Ledger Shipped => new(OrderStatus.Completed, null, 1, 0, 1, 0, 1);
    public (string, int, int, int, int, int) Counts => (Status, Charged, Refunded, Held, Released, Shipments);
}

public static class Requests
{
    public const string GoodCard = "4242-4242-4242-4242";
    public const string GoodAddress = "10 Main Street";

    public static CheckoutRequest Make(string sku = "mug", int quantity = 1, string card = GoodCard, string address = GoodAddress, string? clientRef = null) =>
        new(clientRef ?? Guid.NewGuid().ToString("N"), 7, sku, quantity, card, address);

    public static async Task<(HttpStatusCode Status, JsonElement Body)> CheckoutAsync(this HttpClient client, CheckoutRequest request, string? key = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/checkout") { Content = JsonContent.Create(request) };
        if (key is not null)
        {
            message.Headers.Add(Endpoints.KeyHeader, key);
        }
        using var response = await client.SendAsync(message);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public static long OrderId(this JsonElement body) => body.GetProperty("orderId").GetInt64();
}

public static class Eventually
{
    // Polls until check passes or the timeout runs out, then runs it once more so its assertion
    // failure is the one reported.
    public static async Task AssertAsync(Action check, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            try
            {
                check();
                return;
            }
            catch (Exception)
            {
                await Task.Delay(50);
            }
        }
        check();
    }
}
