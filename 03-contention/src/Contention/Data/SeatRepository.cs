using Dapper;

namespace Contention;

public sealed record Seat(long Id, long SaleId, string Label, string Status, long? BuyerId);

// Reserved seats, for the endpoints and for stage 4.
public sealed class SeatRepository(Databases db)
{
    static SeatRepository() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public async Task<Seat?> GetAsync(long seatId, CancellationToken ct)
    {
        await using var conn = await db.Sales.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Seat>(new CommandDefinition(
            "SELECT id, sale_id, label, status, buyer_id FROM seats WHERE id = @seatId", new { seatId }, cancellationToken: ct));
    }

    // Marks the seat sold to buyerId if it is still available. False if it was already sold.
    public async Task<bool> TrySellAsync(long seatId, long buyerId, CancellationToken ct)
    {
        await using var conn = await db.Sales.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE seats SET status = 'sold', buyer_id = @buyerId WHERE id = @seatId AND status = 'available'",
            new { seatId, buyerId }, cancellationToken: ct)) == 1;
    }

    // The endpoints call this after every purchase they report as Bought, so a seat sold twice shows
    // up as two orders. Your code does not need to.
    public async Task RecordOrderAsync(long seatId, long buyerId, CancellationToken ct)
    {
        await using var conn = await db.Sales.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO seat_orders (seat_id, buyer_id) VALUES (@seatId, @buyerId)", new { seatId, buyerId }, cancellationToken: ct));
    }
}
