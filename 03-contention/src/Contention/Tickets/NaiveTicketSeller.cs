using Dapper;

namespace Contention;

// The baseline: read, decide, check, write. Correct for one buyer at a time. Under a flash sale,
// a hundred buyers read sold = 99 at the same moment and all of them write.
public sealed class NaiveTicketSeller(Databases db, PurchaseCheck check) : ITicketSeller
{
    public async Task<PurchaseResult> BuyAsync(long saleId, long buyerId, CancellationToken ct)
    {
        await using var conn = await db.Sales.OpenConnectionAsync(ct);
        var sale = await conn.QuerySingleOrDefaultAsync<(int Capacity, int Sold, int PerBuyerLimit)?>(new CommandDefinition(
            "SELECT capacity, sold, per_buyer_limit FROM sales WHERE id = @saleId", new { saleId }, cancellationToken: ct));
        if (sale is not { } s)
        {
            return PurchaseResult.Of(Outcome.NotFound);
        }
        if (s.Sold >= s.Capacity)
        {
            return PurchaseResult.Of(Outcome.SoldOut);
        }
        var mine = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM tickets WHERE sale_id = @saleId AND buyer_id = @buyerId", new { saleId, buyerId }, cancellationToken: ct));
        if (mine >= s.PerBuyerLimit)
        {
            return PurchaseResult.Of(Outcome.LimitReached);
        }

        await check.RunAsync(buyerId, null, ct);

        var ticketId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO tickets (sale_id, buyer_id) VALUES (@saleId, @buyerId) RETURNING id", new { saleId, buyerId }, cancellationToken: ct));
        // what we read plus one: a lost update when two buyers read the same value
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sales SET sold = @sold WHERE id = @saleId", new { saleId, sold = s.Sold + 1 }, cancellationToken: ct));
        return PurchaseResult.Bought(ticketId);
    }
}
