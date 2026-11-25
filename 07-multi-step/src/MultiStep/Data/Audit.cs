using Dapper;
using Npgsql;

namespace MultiStep;

public sealed record AuditReport(
    Dictionary<string, long> Orders,
    // purchases (client_ref) with more than one charge that was not refunded
    long ChargedTwice,
    // charges not refunded, on orders that never shipped
    long PaidNotShipped,
    // shipments on orders whose charge is missing or refunded
    long ShippedNotPaid,
    // reservations still held, on orders that never shipped
    long HeldNotShipped,
    // units missing or extra: stocked - available - held, summed over products
    long StockMismatch);

// Checks the end state across the three services. Run it when no checkout is in flight.
public sealed class Audit(NpgsqlDataSource db)
{
    public async Task<AuditReport> ReportAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var orders = (await conn.QueryAsync<(string Status, long Count)>(new CommandDefinition(
            "SELECT status, count(*) FROM orders GROUP BY status", cancellationToken: ct))).ToDictionary(r => r.Status, r => r.Count);
        var row = await conn.QuerySingleAsync<(long ChargedTwice, long PaidNotShipped, long ShippedNotPaid, long HeldNotShipped, long StockMismatch)>(
            new CommandDefinition("""
            SELECT
              (SELECT count(*) FROM (SELECT o.client_ref FROM payments p JOIN orders o ON o.id = p.order_id
                                     WHERE p.status = 'charged' GROUP BY o.client_ref HAVING count(*) > 1) t),
              (SELECT count(*) FROM payments p WHERE p.status = 'charged'
                 AND NOT EXISTS (SELECT 1 FROM shipments s WHERE s.order_id = p.order_id)),
              (SELECT count(*) FROM shipments s
                 WHERE NOT EXISTS (SELECT 1 FROM payments p WHERE p.order_id = s.order_id AND p.status = 'charged')),
              (SELECT count(*) FROM reservations r WHERE r.status = 'held'
                 AND NOT EXISTS (SELECT 1 FROM shipments s WHERE s.order_id = r.order_id)),
              (SELECT COALESCE(sum(abs(p.stocked - p.available - COALESCE(h.held, 0))), 0)::bigint FROM products p
                 LEFT JOIN (SELECT sku, sum(quantity) AS held FROM reservations WHERE status = 'held' GROUP BY sku) h ON h.sku = p.sku)
            """, cancellationToken: ct));
        return new(orders, row.ChargedTwice, row.PaidNotShipped, row.ShippedNotPaid, row.HeldNotShipped, row.StockMismatch);
    }
}
