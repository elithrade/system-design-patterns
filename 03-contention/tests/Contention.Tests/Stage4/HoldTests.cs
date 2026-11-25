namespace Contention.Tests.Stage4;

// Stage 4 goal: one buyer at a time holds a seat, a hold frees itself, and only the holder buys.
[Collection("infra")]
public sealed class HoldTests(InfraFixture infra)
{
    private async Task<App> StartAsync(int holdSeconds = 60) =>
        App.Create(infra, await infra.CloneSalesAsync(), seats: SeatStrategy.Held,
            extra: new() { ["Sale:HoldSeconds"] = holdSeconds.ToString() });

    [Fact]
    public async Task Of_two_hundred_buyers_one_holds_the_seat()
    {
        await using var app = await StartAsync();

        var results = await Concurrently.RunAsync(200, i => app.As(1 + i).HoldAsync(5));

        Assert.Single(results, r => r.Outcome == Outcome.Held);
        Assert.Equal(199, results.Count(r => r.Outcome == Outcome.HeldByOther));
    }

    [Fact]
    public async Task A_hold_says_when_it_expires()
    {
        await using var app = await StartAsync(holdSeconds: 60);

        var held = await app.As(1).HoldAsync(5);

        Assert.Equal(Outcome.Held, held.Outcome);
        Assert.Equal(5, held.Id);
        Assert.Equal(app.Time.GetUtcNow().AddSeconds(60), held.ExpiresAt);
    }

    [Fact]
    public async Task The_holder_buys_the_seat_and_the_hold_goes_away()
    {
        await using var app = await StartAsync();
        await app.As(1).HoldAsync(5);

        var bought = await app.As(1).PurchaseSeatAsync(5);

        Assert.Equal(Outcome.Bought, bought.Outcome);
        Assert.Equal(5, bought.Id);
        var seat = await app.CreateClient().SeatAsync(5);
        Assert.Equal(("sold", 1L), (seat.Status, seat.BuyerId!.Value));
        Assert.Equal(Outcome.Taken, (await app.As(2).HoldAsync(5)).Outcome);
    }

    [Fact]
    public async Task Without_the_hold_you_cannot_buy()
    {
        await using var app = await StartAsync();

        Assert.Equal(Outcome.NotHeld, (await app.As(1).PurchaseSeatAsync(5)).Outcome);
        await app.As(2).HoldAsync(5);
        Assert.Equal(Outcome.NotHeld, (await app.As(1).PurchaseSeatAsync(5)).Outcome);
        Assert.Equal("available", (await app.CreateClient().SeatAsync(5)).Status);
    }

    [Fact]
    public async Task Only_the_holder_can_release()
    {
        await using var app = await StartAsync();
        await app.As(1).HoldAsync(5);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, await app.As(2).ReleaseAsync(5));
        Assert.Equal(Outcome.HeldByOther, (await app.As(2).HoldAsync(5)).Outcome);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, await app.As(1).ReleaseAsync(5));
        Assert.Equal(Outcome.Held, (await app.As(2).HoldAsync(5)).Outcome);
    }

    [Fact]
    public async Task An_expired_hold_frees_the_seat()
    {
        await using var app = await StartAsync(holdSeconds: 1);
        await app.As(1).HoldAsync(5);

        // Redis expires keys on its own clock, so this waits in real time
        await Task.Delay(1500);

        Assert.Equal(Outcome.Held, (await app.As(2).HoldAsync(5)).Outcome);
        Assert.Equal(Outcome.NotHeld, (await app.As(1).PurchaseSeatAsync(5)).Outcome);
    }

    [Fact]
    public async Task The_holder_clicking_buy_ten_times_buys_once()
    {
        await using var app = await StartAsync();
        await app.As(1).HoldAsync(5);
        var buyer = app.As(1);

        var results = await Concurrently.RunAsync(10, _ => buyer.PurchaseSeatAsync(5));

        Assert.Single(results, r => r.Outcome == Outcome.Bought);
        Assert.All(results.Where(r => r.Outcome != Outcome.Bought),
            r => Assert.Contains(r.Outcome, new[] { Outcome.Taken, Outcome.NotHeld }));
        Assert.Equal(1, (await app.CreateClient().AuditAsync()).SeatOrders);
    }

    [Fact]
    public async Task Missing_and_sold_seats()
    {
        await using var app = await StartAsync();

        Assert.Equal(Outcome.NotFound, (await app.As(1).HoldAsync(999)).Outcome);
        await app.As(1).HoldAsync(7);
        await app.As(1).PurchaseSeatAsync(7);
        Assert.Equal(Outcome.Taken, (await app.As(2).HoldAsync(7)).Outcome);
    }
}
