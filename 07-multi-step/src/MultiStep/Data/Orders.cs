using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MultiStep;

public sealed record CheckoutRequest(string ClientRef, long CustomerId, string Sku, int Quantity, string Card, string Address);

public sealed record CheckoutResult(long OrderId, string Status, string? Reason);

public static class OrderStatus
{
    public const string Running = "running";
    public const string Compensating = "compensating";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";

    public static bool IsFinished(string status) => status is Completed or Cancelled or Failed;
}

public sealed record Order(
    long Id,
    string OpKey,
    string ClientRef,
    long CustomerId,
    string Sku,
    int Quantity,
    string Card,
    string Address,
    long Amount,
    string Status,
    string? Reason,
    long? ChargeId,
    long? ReservationId,
    long? ShipmentId,
    DateTime? LeaseUntil,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    // The services' idempotency key for one step of this order: the same on every retry, on every
    // process, for as long as the order exists.
    public string KeyFor(Op op) => Keys.For(OpKey, op);

    public CheckoutResult Result => new(Id, Status, Reason);
}

public sealed class Orders(NpgsqlDataSource db, TimeProvider time, IOptions<CheckoutOptions> options)
{
    private const string Columns = """
        id, op_key, client_ref, customer_id, sku, quantity, card, address, amount, status, reason,
        charge_id, reservation_id, shipment_id, lease_until, created_at, updated_at
        """;

    // The order for opKey: a new running one, leased for LeaseSeconds, or the existing one.
    public async Task<Order> CreateAsync(CheckoutRequest request, string opKey, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<Order>(new CommandDefinition($"""
            INSERT INTO orders (op_key, client_ref, customer_id, sku, quantity, card, address, amount, status, lease_until, created_at, updated_at)
            VALUES (@opKey, @ClientRef, @CustomerId, @Sku, @Quantity, @Card, @Address,
                    COALESCE((SELECT price FROM products WHERE sku = @Sku), 0) * @Quantity, 'running', @leaseUntil, @now, @now)
            ON CONFLICT (op_key) DO UPDATE SET op_key = EXCLUDED.op_key
            RETURNING {Columns}
            """, new { opKey, request.ClientRef, request.CustomerId, request.Sku, request.Quantity, request.Card, request.Address,
                       leaseUntil = now.AddSeconds(options.Value.LeaseSeconds), now }, cancellationToken: ct));
    }

    public async Task<Order?> GetAsync(long orderId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Order>(new CommandDefinition(
            $"SELECT {Columns} FROM orders WHERE id = @orderId", new { orderId }, cancellationToken: ct));
    }

    // Writes status, reason, the three step ids and lease_until; sets updated_at. Returns what was saved.
    public async Task<Order> SaveAsync(Order order, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<Order>(new CommandDefinition($"""
            UPDATE orders SET status = @Status, reason = @Reason, charge_id = @ChargeId, reservation_id = @ReservationId,
                              shipment_id = @ShipmentId, lease_until = @LeaseUntil, updated_at = @now
            WHERE id = @Id
            RETURNING {Columns}
            """, new { order.Id, order.Status, order.Reason, order.ChargeId, order.ReservationId, order.ShipmentId, order.LeaseUntil,
                       now = time.GetUtcNow() }, cancellationToken: ct));
    }
}
