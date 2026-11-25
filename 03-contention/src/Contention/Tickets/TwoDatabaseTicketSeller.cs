using Dapper;
using Microsoft.Extensions.Options;

namespace Contention;

// The README's last section. A locking seller, except that the payment is written to a second
// database. Both writes succeed or fail on their own: no transaction spans two databases.
//
// CrashAfterPaymentRate simulates the process dying between the payment commit and the ticket
// commit (a deploy, an OOM kill). GET /payments/audit shows what that leaves behind.
public sealed class TwoDatabaseTicketSeller(Databases db, PurchaseCheck check, IOptions<SaleOptions> options) : ITicketSeller
{
    private readonly double _crashRate = options.Value.CrashAfterPaymentRate;

    public async Task<PurchaseResult> BuyAsync(long saleId, long buyerId, CancellationToken ct)
    {
        await using var sales = await db.Sales.OpenConnectionAsync(ct);
        await using var tx = await sales.BeginTransactionAsync(ct);
        var sale = await sales.QuerySingleOrDefaultAsync<(int Capacity, int Sold, int PerBuyerLimit)?>(new CommandDefinition(
            "SELECT capacity, sold, per_buyer_limit FROM sales WHERE id = @saleId FOR UPDATE", new { saleId }, tx, cancellationToken: ct));
        if (sale is not { } s)
        {
            return PurchaseResult.Of(Outcome.NotFound);
        }
        if (s.Sold >= s.Capacity)
        {
            return PurchaseResult.Of(Outcome.SoldOut);
        }
        var mine = await sales.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM tickets WHERE sale_id = @saleId AND buyer_id = @buyerId", new { saleId, buyerId }, tx, cancellationToken: ct));
        if (mine >= s.PerBuyerLimit)
        {
            return PurchaseResult.Of(Outcome.LimitReached);
        }

        await check.RunAsync(buyerId, null, ct);

        var ticketId = await sales.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO tickets (sale_id, buyer_id) VALUES (@saleId, @buyerId) RETURNING id", new { saleId, buyerId }, tx, cancellationToken: ct));
        await sales.ExecuteAsync(new CommandDefinition(
            "UPDATE sales SET sold = sold + 1 WHERE id = @saleId", new { saleId }, tx, cancellationToken: ct));

        await using (var payments = await db.Payments!.OpenConnectionAsync(ct))
        {
            await payments.ExecuteAsync(new CommandDefinition(
                "INSERT INTO payments (ticket_id, buyer_id, amount) VALUES (@ticketId, @buyerId, 49.00)",
                new { ticketId, buyerId }, cancellationToken: ct));
        }

        if (Random.Shared.NextDouble() < _crashRate)
        {
            // the payment is committed; the ticket transaction rolls back when tx is disposed
            throw new SimulatedCrashException();
        }
        await tx.CommitAsync(ct);
        return PurchaseResult.Bought(ticketId);
    }
}

public sealed class SimulatedCrashException() : Exception("simulated crash after the payment committed");
