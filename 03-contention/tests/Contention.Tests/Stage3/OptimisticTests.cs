namespace Contention.Tests.Stage3;

// Stage 3 goal: no locks held across the check, no seat sold twice, and conflicts retried a
// bounded number of times.
[Collection("infra")]
public sealed class OptimisticTests(InfraFixture infra) : IAsyncLifetime
{
    private string _db = null!;
    private App _app = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await infra.CloneSalesAsync();
        _app = App.Create(infra, _db, seats: SeatStrategy.Optimistic, extra: new() { ["Sale:MaxAttempts"] = "3" });
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    // what a concurrent writer does to the seat: any change moves the version on
    private Task BumpVersionAsync(long seatId) =>
        Sql.ExecAsync(_db, "UPDATE seats SET version = version + 1 WHERE id = @seatId", new { seatId });

    [Fact]
    public async Task Three_hundred_buyers_at_once_never_share_a_seat()
    {
        var results = await Concurrently.RunAsync(300, i => _app.As(1 + i).BuyBestSeatAsync());

        var bought = results.Where(r => r.Outcome == Outcome.Bought).ToList();
        Assert.Equal(bought.Count, bought.Select(r => r.Id).Distinct().Count());
        Assert.All(results, r => Assert.Contains(r.Outcome, new[] { Outcome.Bought, Outcome.SoldOut, Outcome.Busy }));
        var audit = await _app.CreateClient().AuditAsync();
        Assert.Equal(0, audit.SeatsSoldTwice);
        Assert.Equal(bought.Count, audit.SeatsSold);
        TestContext.Current.SendDiagnosticMessage($"{bought.Count} bought, {_app.Stats.Conflicts} conflicts, {results.Count(r => r.Outcome == Outcome.Busy)} busy");
    }

    [Fact]
    public async Task One_at_a_time_buyers_get_seats_in_order_then_sold_out()
    {
        for (var buyer = 1; buyer <= InfraFixture.Capacity; buyer++)
        {
            var result = await _app.As(buyer).BuyBestSeatAsync();
            Assert.Equal(Outcome.Bought, result.Outcome);
            Assert.Equal(buyer, result.Id);
        }

        Assert.Equal(Outcome.SoldOut, (await _app.As(999).BuyBestSeatAsync()).Outcome);
        Assert.Equal(0, _app.Stats.Conflicts);
    }

    [Fact]
    public async Task A_change_between_read_and_write_is_retried()
    {
        var changed = 0;
        _app.Check.Hook = async (_, seatId) =>
        {
            if (Interlocked.Increment(ref changed) == 1)
            {
                await BumpVersionAsync(seatId!.Value);
            }
        };

        var result = await _app.As(5).BuyBestSeatAsync();

        Assert.Equal(Outcome.Bought, result.Outcome);
        Assert.Equal(1, _app.Stats.Conflicts);
        Assert.Equal(5, (await _app.CreateClient().SeatAsync(result.Id!.Value)).BuyerId);
    }

    [Fact]
    public async Task Constant_changes_give_up_after_max_attempts()
    {
        _app.Check.Hook = (_, seatId) => BumpVersionAsync(seatId!.Value);

        // a seller that retries forever never answers, so give up after 10 s
        var result = await _app.As(5).BuyBestSeatAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Outcome.Busy, result.Outcome);
        Assert.Equal(3, _app.Stats.Conflicts);
        Assert.Equal(0, (await _app.CreateClient().AuditAsync()).SeatsSold);
    }

    [Fact]
    public async Task A_missing_sale_is_not_found()
    {
        Assert.Equal(Outcome.NotFound, (await _app.As(1).BuyBestSeatAsync(saleId: 99)).Outcome);
    }
}
