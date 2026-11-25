namespace MultiStep.Tests.Stage3;

// Stage 3 goal: the process can die at any point and another one finishes the job, once.
// ProcessCrashException stands in for the process dying.
[Collection("infra")]
public sealed class RecoveryTests(InfraFixture infra)
{
    private static readonly CancellationToken None = CancellationToken.None;

    // Runs a checkout that crashes; returns its order id.
    private static async Task<long> CrashAsync(App app, CheckoutRequest request, Op after, string opKey = "op-1")
    {
        app.Faults.CrashAfter(after);
        await Assert.ThrowsAsync<ProcessCrashException>(() => app.Saga.RunAsync(request, opKey, None));
        var order = await app.Orders.CreateAsync(request, opKey, None);
        Assert.False(OrderStatus.IsFinished(order.Status), $"the crashed order is {order.Status}");
        return order.Id;
    }

    private static void PassTheLease(App app) => app.Time.Advance(TimeSpan.FromSeconds(App.LeaseSeconds + 1));

    [Fact]
    public async Task A_crash_after_charging_is_finished_by_recovery_with_one_charge()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);
        var id = await CrashAsync(app, Requests.Make(), after: Op.Charge);

        PassTheLease(app);
        Assert.Equal(1, await app.Recovery.RecoverAsync(None));

        Assert.Equal(Ledger.Shipped.Counts, (await app.LedgerAsync(id)).Counts);
    }

    [Fact]
    public async Task A_crash_after_reserving_takes_the_stock_once()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);
        var id = await CrashAsync(app, Requests.Make(quantity: 4), after: Op.Reserve);

        PassTheLease(app);
        await app.Recovery.RecoverAsync(None);

        Assert.Equal(Ledger.Shipped.Counts, (await app.LedgerAsync(id)).Counts);
        Assert.Equal(996, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task An_order_still_under_lease_is_left_alone()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);
        var id = await CrashAsync(app, Requests.Make(), after: Op.Charge);

        app.Time.Advance(TimeSpan.FromSeconds(App.LeaseSeconds - 5));
        Assert.Equal(0, await app.Recovery.RecoverAsync(None));

        Assert.Equal(1, app.Faults.Calls(Op.Charge));
        Assert.Equal(OrderStatus.Running, (await app.LedgerAsync(id)).Status);
    }

    [Fact]
    public async Task A_decision_to_cancel_survives_a_crash_while_undoing()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);
        // shipping fails until the saga gives up, then would work again: going forward after the
        // crash would ship an order the saga already decided to cancel
        app.Faults.Fail<ServiceUnavailableException>(Op.Ship, App.MaxAttempts);
        var id = await CrashAsync(app, Requests.Make(), after: Op.Release);

        PassTheLease(app);
        await app.Recovery.RecoverAsync(None);

        Assert.Equal((OrderStatus.Cancelled, 0, 1, 0, 1, 0), (await app.LedgerAsync(id)).Counts);
        Assert.Equal(1000, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task A_crash_during_recovery_leaves_the_order_for_the_next_round()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);
        var id = await CrashAsync(app, Requests.Make(), after: Op.Charge);
        PassTheLease(app);
        app.Faults.CrashAfter(Op.Reserve);

        await Assert.ThrowsAsync<ProcessCrashException>(() => app.Recovery.RecoverAsync(None));
        Assert.Equal(0, await app.Recovery.RecoverAsync(None));
        PassTheLease(app);
        Assert.Equal(1, await app.Recovery.RecoverAsync(None));

        Assert.Equal(Ledger.Shipped.Counts, (await app.LedgerAsync(id)).Counts);
        Assert.Equal(999, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task Finished_orders_are_not_resumed()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga);
        await app.Checkout.RunAsync(Requests.Make(), "done", None);
        await app.Checkout.RunAsync(Requests.Make(card: Payments.DeclinedCard), "cancelled", None);

        PassTheLease(app);

        Assert.Equal(0, await app.Recovery.RecoverAsync(None));
    }

    [Fact]
    public async Task Three_recoverers_at_once_resume_each_order_once()
    {
        await using var app = await App.StartAsync(infra, Engine.Saga, new() { ["Faults:LatencyMs"] = "5" });
        var ids = new List<long>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await CrashAsync(app, Requests.Make(), after: Op.Charge, opKey: $"op-{i}"));
        }
        PassTheLease(app);

        var resumed = await Concurrently.RunAsync(3, async _ =>
        {
            var total = 0;
            while (await app.Recovery.RecoverAsync(None) is var n and > 0)
            {
                total += n;
            }
            return total;
        });

        Assert.Equal(6, resumed.Sum());
        foreach (var id in ids)
        {
            Assert.Equal(Ledger.Shipped.Counts, (await app.LedgerAsync(id)).Counts);
        }
    }
}
