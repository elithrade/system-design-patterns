using Temporalio.Client;

namespace MultiStep.Tests.Stage4;

// Stage 4 goal: the stage 2 and 3 behavior again, from a Temporal workflow. Each test has its own
// task queue on a shared Temporal dev server.
[Collection("temporal")]
public sealed class TemporalTests(TemporalFixture temporal)
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static Task<App> StartAsync(TemporalFixture temporal, string taskQueue, string? db = null) =>
        App.StartAsync(temporal, Engine.Temporal, new()
        {
            ["Temporal:Address"] = temporal.Address,
            ["Temporal:TaskQueue"] = taskQueue,
            ["Temporal:ActivityTimeoutSeconds"] = "2",
            ["Checkout:RetryMilliseconds"] = "50",
        }, db);

    private static async Task<(App App, CheckoutResult Result)> RunAsync(TemporalFixture temporal, CheckoutRequest request, Action<Faults>? faults = null)
    {
        var app = await StartAsync(temporal, $"q-{Guid.NewGuid():N}");
        faults?.Invoke(app.Faults);
        var result = await app.Checkout.RunAsync(request, Guid.NewGuid().ToString("N"), None).WaitAsync(TimeSpan.FromSeconds(30));
        return (app, result);
    }

    [Fact]
    public async Task A_checkout_with_no_failures_completes()
    {
        var (app, result) = await RunAsync(temporal, Requests.Make(quantity: 2));
        await using var _ = app;

        Assert.Equal(OrderStatus.Completed, result.Status);
        Assert.Equal(Ledger.Shipped.Counts, (await app.LedgerAsync(result.OrderId)).Counts);
        Assert.Equal(998, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task A_declined_card_cancels_without_retrying()
    {
        var (app, result) = await RunAsync(temporal, Requests.Make(card: Payments.DeclinedCard));
        await using var _ = app;

        Assert.Equal(OrderStatus.Cancelled, result.Status);
        Assert.Contains("declined", result.Reason);
        Assert.Equal((OrderStatus.Cancelled, 0, 0, 0, 0, 0), (await app.LedgerAsync(result.OrderId)).Counts);
        Assert.Equal(1, app.Faults.Calls(Op.Charge));
    }

    [Fact]
    public async Task An_undeliverable_address_is_undone_even_when_the_undo_fails_at_first()
    {
        var (app, result) = await RunAsync(temporal, Requests.Make(address: Shipping.UndeliverableAddress),
            f => f.Fail<ServiceUnavailableException>(Op.Refund, App.MaxAttempts + 2));
        await using var _ = app;

        Assert.Equal(OrderStatus.Cancelled, result.Status);
        Assert.Equal((OrderStatus.Cancelled, 0, 1, 0, 1, 0), (await app.LedgerAsync(result.OrderId)).Counts);
        Assert.Equal(1000, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task Transient_failures_and_lost_responses_are_retried_without_doing_the_work_twice()
    {
        var (app, result) = await RunAsync(temporal, Requests.Make(), f =>
        {
            f.Fail<ServiceUnavailableException>(Op.Charge, 2);
            f.Fail<LostResponseException>(Op.Reserve);
            f.Fail<LostResponseException>(Op.Ship);
        });
        await using var _ = app;

        Assert.Equal(Ledger.Shipped.Counts, (await app.LedgerAsync(result.OrderId)).Counts);
        Assert.Equal(999, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task A_step_out_of_attempts_is_undone()
    {
        var (app, result) = await RunAsync(temporal, Requests.Make(),
            f => f.Fail<ServiceUnavailableException>(Op.Ship, App.MaxAttempts));
        await using var _ = app;

        Assert.Equal((OrderStatus.Cancelled, 0, 1, 0, 1, 0), (await app.LedgerAsync(result.OrderId)).Counts);
        Assert.Equal(App.MaxAttempts, app.Faults.Calls(Op.Ship));
    }

    [Fact]
    public async Task A_charge_whose_every_response_was_lost_is_still_refunded()
    {
        var (app, result) = await RunAsync(temporal, Requests.Make(),
            f => f.Fail<LostResponseException>(Op.Charge, App.MaxAttempts));
        await using var _ = app;

        Assert.Equal((OrderStatus.Cancelled, 0, 1, 0, 0, 0), (await app.LedgerAsync(result.OrderId)).Counts);
    }

    [Fact]
    public async Task The_same_checkout_started_twice_runs_once()
    {
        await using var app = await StartAsync(temporal, $"q-{Guid.NewGuid():N}");
        var request = Requests.Make();

        var results = await Concurrently.RunAsync(2, _ => app.Checkout.RunAsync(request, "same-op", None).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(results[0], results[1]);
        Assert.Equal(1, app.Faults.Calls(Op.Charge));
    }

    [Fact]
    public async Task When_a_worker_dies_mid_step_another_finishes_the_checkout_once()
    {
        var queue = $"q-{Guid.NewGuid():N}";
        var dying = await StartAsync(temporal, queue);
        var db = dying.Db;
        dying.Faults.HangAfter(Op.Charge);
        var started = dying.Checkout.RunAsync(Requests.Make(), "op-1", None);
        await Eventually.AssertAsync(() => Assert.Equal(1, dying.Faults.Calls(Op.Charge)), 10_000);

        await dying.DisposeAsync();
        await using var survivor = await StartAsync(temporal, queue, db);
        var result = await temporal.Client.GetWorkflowHandle(TemporalCheckout.WorkflowId("op-1"))
            .GetResultAsync<CheckoutResult>().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OrderStatus.Completed, result.Status);
        Assert.Equal(Ledger.Shipped.Counts, (await survivor.LedgerAsync(result.OrderId)).Counts);
        _ = started.ContinueWith(t => t.Exception, TaskScheduler.Default);
    }
}
