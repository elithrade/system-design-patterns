using System.Net;

namespace MultiStep.Tests.Stage1;

// Stage 1 goal: a client can retry POST /checkout with the same Idempotency-Key as often as it
// likes, and the purchase happens once. These run on the baseline engine.
[Collection("infra")]
public sealed class IdempotencyTests(InfraFixture infra)
{
    [Fact]
    public async Task The_same_key_twice_returns_the_same_response_and_charges_once()
    {
        await using var app = await App.StartAsync(infra, Engine.Naive);
        var client = app.CreateClient();
        var request = Requests.Make();

        var first = await client.CheckoutAsync(request, "key-1");
        var second = await client.CheckoutAsync(request, "key-1");

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal((first.Status, first.Body.ToString()), (second.Status, second.Body.ToString()));
        Assert.Equal(1, app.Faults.Calls(Op.Charge));
        Assert.Equal(Ledger.Shipped.Counts, (await app.LedgerAsync(first.Body.OrderId())).Counts);
    }

    [Fact]
    public async Task Ten_requests_with_one_key_at_once_buy_once()
    {
        await using var app = await App.StartAsync(infra, Engine.Naive, new() { ["Faults:LatencyMs"] = "20" });
        var client = app.CreateClient();
        var request = Requests.Make();

        var responses = await Concurrently.RunAsync(10, _ => client.CheckoutAsync(request, "key-1"));

        Assert.Contains(responses, r => r.Status == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.Contains(r.Status, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
        Assert.Equal(1, app.Faults.Calls(Op.Charge));
        Assert.Equal(999, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task A_key_reused_for_a_different_request_is_refused()
    {
        await using var app = await App.StartAsync(infra, Engine.Naive);
        var client = app.CreateClient();
        await client.CheckoutAsync(Requests.Make(quantity: 1), "key-1");

        var (status, _) = await client.CheckoutAsync(Requests.Make(quantity: 5), "key-1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(999, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task A_retry_after_a_failure_halfway_finishes_the_purchase_once()
    {
        await using var app = await App.StartAsync(infra, Engine.Naive);
        var client = app.CreateClient();
        var request = Requests.Make();
        app.Faults.Fail<LostResponseException>(Op.Ship);

        var first = await client.CheckoutAsync(request, "key-1");
        var retry = await client.CheckoutAsync(request, "key-1");

        Assert.Equal((HttpStatusCode.ServiceUnavailable, HttpStatusCode.Created), (first.Status, retry.Status));
        Assert.Equal(Ledger.Shipped.Counts, (await app.LedgerAsync(retry.Body.OrderId())).Counts);
        var audit = await app.AuditAsync();
        Assert.Equal((0L, 0L), (audit.ChargedTwice, audit.StockMismatch));
    }

    [Fact]
    public async Task A_decided_failure_is_replayed_not_retried()
    {
        await using var app = await App.StartAsync(infra, Engine.Naive);
        var client = app.CreateClient();
        var request = Requests.Make(card: Payments.DeclinedCard);

        var first = await client.CheckoutAsync(request, "key-1");
        var second = await client.CheckoutAsync(request, "key-1");

        Assert.Equal(HttpStatusCode.Conflict, first.Status);
        Assert.Equal((first.Status, first.Body.ToString()), (second.Status, second.Body.ToString()));
        Assert.Equal(1, app.Faults.Calls(Op.Charge));
    }

    [Fact]
    public async Task A_key_left_locked_by_a_crash_can_be_retried_after_the_lock_expires()
    {
        await using var app = await App.StartAsync(infra, Engine.Naive);
        var client = app.CreateClient();
        var request = Requests.Make();
        app.Faults.CrashAfter(Op.Reserve);

        var crashed = await client.CheckoutAsync(request, "key-1");
        var tooSoon = await client.CheckoutAsync(request, "key-1");
        app.Time.Advance(TimeSpan.FromSeconds(App.KeyLockSeconds + 1));
        var later = await client.CheckoutAsync(request, "key-1");

        Assert.Equal((HttpStatusCode.InternalServerError, HttpStatusCode.Conflict, HttpStatusCode.Created), (crashed.Status, tooSoon.Status, later.Status));
        Assert.Equal(Ledger.Shipped.Counts, (await app.LedgerAsync(later.Body.OrderId())).Counts);
        Assert.Equal(999, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task Different_keys_are_different_purchases()
    {
        await using var app = await App.StartAsync(infra, Engine.Naive);
        var client = app.CreateClient();

        var a = await client.CheckoutAsync(Requests.Make(), "key-a");
        var b = await client.CheckoutAsync(Requests.Make(), "key-b");

        Assert.NotEqual(a.Body.OrderId(), b.Body.OrderId());
        Assert.Equal(998, await app.AvailableAsync("mug"));
    }
}
