namespace Contention.Tests.Stage1;

// Stage 1 goal: 1,000 buyers at once for 100 tickets sell exactly 100.
[Collection("infra")]
public sealed class ConditionalUpdateTests(InfraFixture infra) : IAsyncLifetime
{
    private App _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = App.Create(infra, await infra.CloneSalesAsync(), tickets: TicketStrategy.Conditional);

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task A_thousand_buyers_at_once_buy_exactly_the_capacity()
    {
        var results = await Concurrently.RunAsync(1000, i => _app.As(1 + i).BuyTicketAsync());

        Assert.Equal(InfraFixture.Capacity, results.Count(r => r.Outcome == Outcome.Bought));
        Assert.All(results.Where(r => r.Outcome != Outcome.Bought), r => Assert.Equal(Outcome.SoldOut, r.Outcome));
        Assert.Equal(InfraFixture.Capacity, results.Where(r => r.Outcome == Outcome.Bought).Select(r => r.Id).Distinct().Count());
        var audit = await _app.CreateClient().AuditAsync();
        Assert.Equal(InfraFixture.Capacity, audit.Tickets);
        Assert.Equal(InfraFixture.Capacity, audit.Sold);
    }

    [Fact]
    public async Task One_at_a_time_the_limit_holds()
    {
        var buyer = _app.As(7);

        Assert.Equal(Outcome.Bought, (await buyer.BuyTicketAsync()).Outcome);
        Assert.Equal(Outcome.Bought, (await buyer.BuyTicketAsync()).Outcome);
        Assert.Equal(Outcome.LimitReached, (await buyer.BuyTicketAsync()).Outcome);
        Assert.Equal(2, (await _app.CreateClient().AuditAsync()).Tickets);
    }

    [Fact]
    public async Task A_missing_sale_is_not_found()
    {
        Assert.Equal(Outcome.NotFound, (await _app.As(1).BuyTicketAsync(saleId: 99)).Outcome);
    }

    [Fact]
    public async Task After_selling_out_nothing_more_is_written()
    {
        await Concurrently.RunAsync(150, i => _app.As(1 + i).BuyTicketAsync());

        var late = await Concurrently.RunAsync(50, i => _app.As(500 + i).BuyTicketAsync());

        Assert.All(late, r => Assert.Equal(Outcome.SoldOut, r.Outcome));
        var audit = await _app.CreateClient().AuditAsync();
        Assert.Equal(InfraFixture.Capacity, audit.Tickets);
        Assert.Equal(InfraFixture.Capacity, audit.Sold);
    }
}
