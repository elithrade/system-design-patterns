using System.Net;

namespace MultiStep.Tests.Baseline;

// Guards and demonstrations: the baseline works when nothing fails, and these are the ways it goes
// wrong when something does. They pass from the start.
[Collection("infra")]
public sealed class NaiveCheckoutTests(InfraFixture infra)
{
    [Fact]
    public async Task A_checkout_charges_reserves_and_ships()
    {
        await using var app = await App.StartAsync(infra, Engine.Naive);

        var (status, body) = await app.CreateClient().CheckoutAsync(Requests.Make(quantity: 2));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(Ledger.Shipped.Counts, (await app.LedgerAsync(body.OrderId())).Counts);
        Assert.Equal(998, await app.AvailableAsync("mug"));
    }

    [Fact]
    public async Task Without_a_key_a_retry_after_a_lost_response_charges_twice()
    {
        await using var app = await App.StartAsync(infra, Engine.Naive);
        var client = app.CreateClient();
        var request = Requests.Make();
        app.Faults.Fail<LostResponseException>(Op.Ship);

        var (first, _) = await client.CheckoutAsync(request);
        var (retry, _) = await client.CheckoutAsync(request);

        Assert.Equal((HttpStatusCode.ServiceUnavailable, HttpStatusCode.Created), (first, retry));
        Assert.Equal(1, (await app.AuditAsync()).ChargedTwice);
    }

    [Fact]
    public async Task Out_of_stock_leaves_the_card_charged()
    {
        await using var app = await App.StartAsync(infra, Engine.Naive);

        var (status, _) = await app.CreateClient().CheckoutAsync(Requests.Make(sku: "scarce", quantity: 21));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(1, (await app.AuditAsync()).PaidNotShipped);
    }
}
