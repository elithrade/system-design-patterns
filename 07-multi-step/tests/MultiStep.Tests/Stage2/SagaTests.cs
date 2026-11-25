using System.Net;

namespace MultiStep.Tests.Stage2;

// Stage 2 goal: every checkout ends completed or cancelled, never halfway. Transient failures are
// retried; permanent ones undo what was done.
[Collection("infra")]
public sealed class SagaTests(InfraFixture infra)
{
    private static async Task<(App App, CheckoutResult Result)> RunAsync(App app, CheckoutRequest request) =>
        (app, await app.Checkout.RunAsync(request, Guid.NewGuid().ToString("N"), CancellationToken.None));

    [Fact]
    public async Task A_checkout_with_no_failures_completes()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);

        var (status, body) = await app.CreateClient().CheckoutAsync(Requests.Make(quantity: 2));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(Ledger.Shipped.Counts, (await app.LedgerAsync(body.OrderId())).Counts);
        Assert.Equal(998, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task A_declined_card_cancels_without_retrying()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);

        var (_, result) = await RunAsync(app, Requests.Make(card: Payments.DeclinedCard));

        var ledger = await app.LedgerAsync(result.OrderId);
        Assert.Equal((OrderStatus.Cancelled, 0, 0, 0, 0, 0), ledger.Counts);
        Assert.Contains("declined", ledger.Reason);
        Assert.Equal((1, 0), (app.Faults.Calls(Op.Charge), app.Faults.Calls(Op.Reserve)));
    }

    [Fact]
    public async Task Out_of_stock_refunds_the_charge()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);

        var (_, result) = await RunAsync(app, Requests.Make(sku: "scarce", quantity: 21));

        Assert.Equal((OrderStatus.Cancelled, 0, 1, 0, 0, 0), (await app.LedgerAsync(result.OrderId)).Counts);
        Assert.Equal(20, await app.AvailableAsync("scarce"));
    }

    [Fact]
    public async Task An_undeliverable_address_releases_the_stock_and_refunds()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);

        var (status, body) = await app.CreateClient().CheckoutAsync(Requests.Make(quantity: 3, address: Shipping.UndeliverableAddress));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal((OrderStatus.Cancelled, 0, 1, 0, 1, 0), (await app.LedgerAsync(body.OrderId())).Counts);
        Assert.Equal(1000, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task Transient_failures_are_retried()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);
        app.Faults.Fail<ServiceUnavailableException>(Op.Charge, 2);
        app.Faults.Fail<ServiceUnavailableException>(Op.Reserve, 2);
        app.Faults.Fail<ServiceUnavailableException>(Op.Ship, 2);

        var (_, result) = await RunAsync(app, Requests.Make());

        Assert.Equal(Ledger.Shipped.Counts, (await app.LedgerAsync(result.OrderId)).Counts);
        Assert.Equal((3, 3, 3), (app.Faults.Calls(Op.Charge), app.Faults.Calls(Op.Reserve), app.Faults.Calls(Op.Ship)));
    }

    [Fact]
    public async Task Lost_responses_are_retried_without_doing_the_work_twice()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);
        app.Faults.Fail<LostResponseException>(Op.Charge);
        app.Faults.Fail<LostResponseException>(Op.Reserve);
        app.Faults.Fail<LostResponseException>(Op.Ship);

        var (_, result) = await RunAsync(app, Requests.Make());

        Assert.Equal(Ledger.Shipped.Counts, (await app.LedgerAsync(result.OrderId)).Counts);
        Assert.Equal(999, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task A_step_out_of_attempts_is_undone_like_a_permanent_failure()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);
        app.Faults.Fail<ServiceUnavailableException>(Op.Ship, App.MaxAttempts);

        var (_, result) = await RunAsync(app, Requests.Make());

        Assert.Equal((OrderStatus.Cancelled, 0, 1, 0, 1, 0), (await app.LedgerAsync(result.OrderId)).Counts);
        Assert.Equal(App.MaxAttempts, app.Faults.Calls(Op.Ship));
        Assert.Equal(1000, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task A_charge_whose_every_response_was_lost_is_still_refunded()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);
        app.Faults.Fail<LostResponseException>(Op.Charge, App.MaxAttempts);

        var (_, result) = await RunAsync(app, Requests.Make());

        Assert.Equal((OrderStatus.Cancelled, 0, 1, 0, 0, 0), (await app.LedgerAsync(result.OrderId)).Counts);
    }

    [Fact]
    public async Task Undo_steps_are_retried_until_they_succeed()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);
        app.Faults.Fail<ServiceUnavailableException>(Op.Refund, App.MaxAttempts + 2);
        app.Faults.Fail<LostResponseException>(Op.Release, 2);

        var (_, result) = await RunAsync(app, Requests.Make(address: Shipping.UndeliverableAddress));

        Assert.Equal((OrderStatus.Cancelled, 0, 1, 0, 1, 0), (await app.LedgerAsync(result.OrderId)).Counts);
        Assert.Equal(1000, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task Resuming_a_finished_order_calls_nothing()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);
        var (_, result) = await RunAsync(app, Requests.Make());
        var calls = app.Faults.Calls(Op.Charge) + app.Faults.Calls(Op.Reserve) + app.Faults.Calls(Op.Ship);

        var again = await app.Saga.ResumeAsync((await app.Orders.GetAsync(result.OrderId, CancellationToken.None))!, CancellationToken.None);

        Assert.Equal(result, again);
        Assert.Equal(calls, app.Faults.Calls(Op.Charge) + app.Faults.Calls(Op.Reserve) + app.Faults.Calls(Op.Ship));
    }

    [Fact]
    public async Task Under_random_failures_every_order_ends_whole()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga, new()
        {
            ["Faults:TransientRate"] = "0.2",
            ["Faults:LostResponseRate"] = "0.1",
        });
        var requests = Enumerable.Range(0, 40).Select(i => (i % 10) switch
        {
            0 => Requests.Make(card: Payments.DeclinedCard),
            1 => Requests.Make(address: Shipping.UndeliverableAddress),
            2 or 3 => Requests.Make(sku: "scarce", quantity: 3),
            _ => Requests.Make(sku: i % 2 == 0 ? "mug" : "poster"),
        }).ToList();

        var results = await Concurrently.RunAsync(requests.Count, i => app.Checkout.RunAsync(requests[i], $"op-{i}", CancellationToken.None));

        Assert.All(results, r => Assert.Contains(r.Status, new[] { OrderStatus.Completed, OrderStatus.Cancelled }));
        var audit = await app.AuditAsync();
        Assert.Equal((0L, 0L, 0L, 0L, 0L), (audit.ChargedTwice, audit.PaidNotShipped, audit.ShippedNotPaid, audit.HeldNotShipped, audit.StockMismatch));
    }
}
