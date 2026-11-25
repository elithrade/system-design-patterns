namespace Contention.Tests.Stage2;

// Stage 2 goal: rules that span rows (the per-buyer limit) hold under concurrency too.
[Collection("infra")]
public sealed class LockingTests(InfraFixture infra) : IAsyncLifetime
{
    private App _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = App.Create(infra, await infra.CloneSalesAsync(), tickets: TicketStrategy.Locking);

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task A_thousand_buyers_at_once_buy_exactly_the_capacity()
    {
        var results = await Concurrently.RunAsync(1000, i => _app.As(1 + i).BuyTicketAsync());

        Assert.Equal(InfraFixture.Capacity, results.Count(r => r.Outcome == Outcome.Bought));
        var audit = await _app.CreateClient().AuditAsync();
        Assert.Equal(InfraFixture.Capacity, audit.Tickets);
        Assert.Equal(InfraFixture.Capacity, audit.Sold);
    }

    [Fact]
    public async Task One_buyer_clicking_twenty_times_at_once_gets_the_limit()
    {
        var buyer = _app.As(42);

        var results = await Concurrently.RunAsync(20, _ => buyer.BuyTicketAsync());

        Assert.Equal(InfraFixture.PerBuyerLimit, results.Count(r => r.Outcome == Outcome.Bought));
        Assert.Equal(20 - InfraFixture.PerBuyerLimit, results.Count(r => r.Outcome == Outcome.LimitReached));
        Assert.Equal(InfraFixture.PerBuyerLimit, (await _app.CreateClient().AuditAsync()).Tickets);
    }

    [Fact]
    public async Task Many_buyers_each_clicking_three_times_stay_within_both_limits()
    {
        var results = await Concurrently.RunAsync(600, i => _app.As(1 + i / 3).BuyTicketAsync());

        var audit = await _app.CreateClient().AuditAsync();
        Assert.Equal(InfraFixture.Capacity, audit.Tickets);
        Assert.Equal(InfraFixture.Capacity, audit.Sold);
        Assert.Equal(0, audit.BuyersOverLimit);
        Assert.Equal(InfraFixture.Capacity, results.Count(r => r.Outcome == Outcome.Bought));
    }

    [Fact]
    public async Task A_missing_sale_is_not_found()
    {
        Assert.Equal(Outcome.NotFound, (await _app.As(1).BuyTicketAsync(saleId: 99)).Outcome);
    }
}
