namespace Contention.Tests.Baseline;

// Guards: the baseline is correct when buyers take turns. These pass from the start; the rules they
// check hold for every stage too.
[Collection("infra")]
public sealed class OneBuyerAtATimeTests(InfraFixture infra)
{
    [Fact]
    public async Task Tickets_sell_out_at_capacity_and_respect_the_limit()
    {
        await using var app = App.Create(infra, await infra.CloneSalesAsync(), checkMilliseconds: 0);

        Assert.Equal(Outcome.Bought, (await app.As(1).BuyTicketAsync()).Outcome);
        Assert.Equal(Outcome.Bought, (await app.As(1).BuyTicketAsync()).Outcome);
        Assert.Equal(Outcome.LimitReached, (await app.As(1).BuyTicketAsync()).Outcome);
        for (var buyer = 2; buyer <= InfraFixture.Capacity - 1; buyer++)
        {
            Assert.Equal(Outcome.Bought, (await app.As(buyer).BuyTicketAsync()).Outcome);
        }
        Assert.Equal(Outcome.SoldOut, (await app.As(1000).BuyTicketAsync()).Outcome);

        var audit = await app.CreateClient().AuditAsync();
        Assert.Equal(InfraFixture.Capacity, audit.Tickets);
        Assert.Equal(InfraFixture.Capacity, audit.Sold);
        Assert.Equal(Outcome.NotFound, (await app.As(1).BuyTicketAsync(saleId: 99)).Outcome);
    }

    [Fact]
    public async Task Best_seat_goes_in_order()
    {
        await using var app = App.Create(infra, await infra.CloneSalesAsync(), checkMilliseconds: 0);

        Assert.Equal(1, (await app.As(1).BuyBestSeatAsync()).Id);
        Assert.Equal(2, (await app.As(2).BuyBestSeatAsync()).Id);
        Assert.Equal(Outcome.Taken, (await app.As(3).PurchaseSeatAsync(1)).Outcome);
        Assert.Equal(Outcome.Bought, (await app.As(3).PurchaseSeatAsync(50)).Outcome);
        Assert.Equal(3, (await app.As(4).BuyBestSeatAsync()).Id);
    }
}
