using Dapper;

namespace Contention;

// Sells the best (lowest id) available seat of a sale. Sale:Seats picks the implementation.
//   NotFound   no such sale
//   SoldOut    no seat is available
//   Bought(id) the seat's id; the seat is sold to the buyer
// Calls check.RunAsync(buyerId, seatId, ct) once per attempt, before writing.
public interface IBestSeatSeller
{
    Task<PurchaseResult> BuyBestAsync(long saleId, long buyerId, CancellationToken ct);
}

// The baseline for both seat endpoints: read, check, write. Two buyers who read the same free seat
// both buy it, and the second overwrites the first.
public sealed class NaiveSeatSeller(Databases db, SeatRepository seats, PurchaseCheck check) : IBestSeatSeller
{
    public async Task<PurchaseResult> BuyBestAsync(long saleId, long buyerId, CancellationToken ct)
    {
        await using var conn = await db.Sales.OpenConnectionAsync(ct);
        if (await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT id FROM sales WHERE id = @saleId", new { saleId }, cancellationToken: ct)) is null)
        {
            return PurchaseResult.Of(Outcome.NotFound);
        }
        var seatId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM seats WHERE sale_id = @saleId AND status = 'available' ORDER BY id LIMIT 1",
            new { saleId }, cancellationToken: ct));
        if (seatId is not { } id)
        {
            return PurchaseResult.Of(Outcome.SoldOut);
        }
        await check.RunAsync(buyerId, id, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE seats SET status = 'sold', buyer_id = @buyerId WHERE id = @id", new { id, buyerId }, cancellationToken: ct));
        return PurchaseResult.Bought(id);
    }

    // POST /seats/{id}/purchase when there are no holds.
    public async Task<PurchaseResult> BuySeatAsync(long seatId, long buyerId, CancellationToken ct)
    {
        var seat = await seats.GetAsync(seatId, ct);
        if (seat is null)
        {
            return PurchaseResult.Of(Outcome.NotFound);
        }
        if (seat.Status == "sold")
        {
            return PurchaseResult.Of(Outcome.Taken);
        }
        await check.RunAsync(buyerId, seatId, ct);
        await using var conn = await db.Sales.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE seats SET status = 'sold', buyer_id = @buyerId WHERE id = @seatId", new { seatId, buyerId }, cancellationToken: ct));
        return PurchaseResult.Bought(seatId);
    }
}
