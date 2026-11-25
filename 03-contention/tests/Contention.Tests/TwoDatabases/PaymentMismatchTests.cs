using System.Net.Http.Json;

namespace Contention.Tests.TwoDatabases;

// Not a stage: a demonstration for the README's last section. Passes from the start.
// With payments in a second database, a crash between the two commits leaves money taken for a
// ticket that does not exist, and none of the locks from stages 1-3 can prevent it.
[Collection("infra")]
public sealed class PaymentMismatchTests(InfraFixture infra)
{
    [Fact]
    public async Task A_crash_between_the_commits_leaves_payments_without_tickets()
    {
        await using var app = App.Create(infra, await infra.CloneSalesAsync(), tickets: TicketStrategy.TwoDatabases,
            payments: await infra.ClonePaymentsAsync(),
            extra: new() { ["Sale:CrashAfterPaymentRate"] = "1" });

        for (var buyer = 1; buyer <= 5; buyer++)
        {
            var response = await app.As(buyer).PostAsync("/sales/1/tickets", null);
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        }

        var audit = await app.CreateClient().GetFromJsonAsync<PaymentAudit>("/payments/audit");
        Assert.Equal(new PaymentAudit(Payments: 5, Tickets: 0, PaymentsWithoutTicket: 5, TicketsWithoutPayment: 0), audit);
    }
}
