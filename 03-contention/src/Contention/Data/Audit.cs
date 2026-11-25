using Dapper;

namespace Contention;

// What GET /sales/{id} reports: whether the sale kept its promises.
public sealed record SaleAudit(
    long Id,
    int Capacity,
    // the sales.sold column
    int Sold,
    // rows in tickets: more than Capacity means oversold; different from Sold means a lost update
    long Tickets,
    // buyers holding more than per_buyer_limit tickets
    long BuyersOverLimit,
    long SeatsSold,
    // seat purchases the app reported as successful
    long SeatOrders,
    // seats with more than one successful purchase
    long SeatsSoldTwice);

// What GET /payments/audit reports: whether the two databases agree.
public sealed record PaymentAudit(long Payments, long Tickets, long PaymentsWithoutTicket, long TicketsWithoutPayment);

public sealed class Audit(Databases db)
{
    // buyers_over_limit -> BuyersOverLimit
    static Audit() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public async Task<SaleAudit?> SaleAsync(long saleId, CancellationToken ct)
    {
        await using var conn = await db.Sales.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SaleAudit>(new CommandDefinition("""
            SELECT s.id, s.capacity, s.sold,
                   (SELECT count(*) FROM tickets t WHERE t.sale_id = s.id) AS tickets,
                   (SELECT count(*) FROM (SELECT buyer_id FROM tickets t WHERE t.sale_id = s.id
                                          GROUP BY buyer_id HAVING count(*) > s.per_buyer_limit) x) AS buyers_over_limit,
                   (SELECT count(*) FROM seats st WHERE st.sale_id = s.id AND st.status = 'sold') AS seats_sold,
                   (SELECT count(*) FROM seat_orders o JOIN seats st ON st.id = o.seat_id WHERE st.sale_id = s.id) AS seat_orders,
                   (SELECT count(*) FROM (SELECT o.seat_id FROM seat_orders o JOIN seats st ON st.id = o.seat_id
                                          WHERE st.sale_id = s.id GROUP BY o.seat_id HAVING count(*) > 1) x) AS seats_sold_twice
            FROM sales s WHERE s.id = @saleId
            """, new { saleId }, cancellationToken: ct));
    }

    // Two databases, so no JOIN: read both sides and compare here.
    public async Task<PaymentAudit> PaymentsAsync(CancellationToken ct)
    {
        await using var sales = await db.Sales.OpenConnectionAsync(ct);
        var tickets = (await sales.QueryAsync<long>(new CommandDefinition("SELECT id FROM tickets", cancellationToken: ct))).ToHashSet();
        await using var payments = await db.Payments!.OpenConnectionAsync(ct);
        var paid = (await payments.QueryAsync<long>(new CommandDefinition("SELECT ticket_id FROM payments", cancellationToken: ct))).ToList();
        return new PaymentAudit(
            paid.Count,
            tickets.Count,
            paid.Count(id => !tickets.Contains(id)),
            tickets.Count(id => !paid.Contains(id)));
    }
}
