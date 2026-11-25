using Dapper;
using Npgsql;

namespace MultiStep;

// The payment service. The same idempotency key charges once and returns the same charge id.
public sealed class Payments(NpgsqlDataSource db, Faults faults, TimeProvider time)
{
    public const string DeclinedCard = "4000-0000-0000-0002";

    public async Task<long> ChargeAsync(string idempotencyKey, long orderId, long amount, string card, CancellationToken ct)
    {
        await faults.BeforeAsync(Op.Charge, ct);
        if (card == DeclinedCard)
        {
            throw new PaymentDeclinedException();
        }
        long id;
        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO payments (idempotency_key, order_id, amount, status, created_at) VALUES (@idempotencyKey, @orderId, @amount, 'charged', @now)
                ON CONFLICT (idempotency_key) DO UPDATE SET idempotency_key = EXCLUDED.idempotency_key
                RETURNING id
                """, new { idempotencyKey, orderId, amount, now = time.GetUtcNow() }, cancellationToken: ct));
        }
        await faults.AfterAsync(Op.Charge, ct);
        return id;
    }

    // Refunds the charge made with that idempotency key. No charge with that key: nothing happens.
    // Refunding twice is fine. Taking the key, not the charge id, lets you undo a charge whose
    // response you never got.
    public async Task RefundAsync(string chargeKey, CancellationToken ct)
    {
        await faults.BeforeAsync(Op.Refund, ct);
        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            await conn.ExecuteAsync(new CommandDefinition("UPDATE payments SET status = 'refunded' WHERE idempotency_key = @chargeKey", new { chargeKey }, cancellationToken: ct));
        }
        await faults.AfterAsync(Op.Refund, ct);
    }
}

// The inventory service. The same idempotency key reserves once.
public sealed class Inventory(NpgsqlDataSource db, Faults faults, TimeProvider time)
{
    public async Task<long> ReserveAsync(string idempotencyKey, long orderId, string sku, int quantity, CancellationToken ct)
    {
        await faults.BeforeAsync(Op.Reserve, ct);
        long id;
        await using (var conn = await db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            // serializes reservations with the same key
            await conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@idempotencyKey))", new { idempotencyKey }, tx, cancellationToken: ct));
            var existing = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT id FROM reservations WHERE idempotency_key = @idempotencyKey", new { idempotencyKey }, tx, cancellationToken: ct));
            if (existing is { } found)
            {
                id = found;
            }
            else
            {
                var taken = await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE products SET available = available - @quantity WHERE sku = @sku AND available >= @quantity",
                    new { sku, quantity }, tx, cancellationToken: ct));
                if (taken == 0)
                {
                    throw new OutOfStockException(sku);
                }
                id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT INTO reservations (idempotency_key, order_id, sku, quantity, status, created_at)
                    VALUES (@idempotencyKey, @orderId, @sku, @quantity, 'held', @now) RETURNING id
                    """, new { idempotencyKey, orderId, sku, quantity, now = time.GetUtcNow() }, tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
        }
        await faults.AfterAsync(Op.Reserve, ct);
        return id;
    }

    // Releases the reservation made with that idempotency key. None: nothing happens. Releasing
    // twice is fine.
    public async Task ReleaseAsync(string reservationKey, CancellationToken ct)
    {
        await faults.BeforeAsync(Op.Release, ct);
        await using (var conn = await db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var released = await conn.QuerySingleOrDefaultAsync<(string Sku, int Quantity)?>(new CommandDefinition(
                "UPDATE reservations SET status = 'released' WHERE idempotency_key = @reservationKey AND status = 'held' RETURNING sku, quantity",
                new { reservationKey }, tx, cancellationToken: ct));
            if (released is var (sku, quantity))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE products SET available = available + @quantity WHERE sku = @sku", new { sku, quantity }, tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
        }
        await faults.AfterAsync(Op.Release, ct);
    }
}

// The shipping service. The same idempotency key creates one shipment. Shipments cannot be undone
// here, so shipping is the last step.
public sealed class Shipping(NpgsqlDataSource db, Faults faults, TimeProvider time)
{
    public const string UndeliverableAddress = "1 Nowhere Lane";

    public async Task<long> ShipAsync(string idempotencyKey, long orderId, string address, CancellationToken ct)
    {
        await faults.BeforeAsync(Op.Ship, ct);
        if (address == UndeliverableAddress)
        {
            throw new UndeliverableException();
        }
        long id;
        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO shipments (idempotency_key, order_id, address, created_at) VALUES (@idempotencyKey, @orderId, @address, @now)
                ON CONFLICT (idempotency_key) DO UPDATE SET idempotency_key = EXCLUDED.idempotency_key
                RETURNING id
                """, new { idempotencyKey, orderId, address, now = time.GetUtcNow() }, cancellationToken: ct));
        }
        await faults.AfterAsync(Op.Ship, ct);
        return id;
    }
}
