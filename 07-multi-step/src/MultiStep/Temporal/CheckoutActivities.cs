using Temporalio.Activities;

namespace MultiStep;

// Temporal activities: the service calls, as a workflow sees them. Each is retried by Temporal
// according to the ActivityOptions the workflow passes. An exception's type name becomes the
// failure's ErrorType (for example "PaymentDeclinedException").
public sealed class CheckoutActivities(Payments payments, Inventory inventory, Shipping shipping, Orders orders)
{
    private static CancellationToken Ct => ActivityExecutionContext.Current.CancellationToken;

    [Activity]
    public Task<long> ChargeAsync(string idempotencyKey, long orderId, long amount, string card) =>
        payments.ChargeAsync(idempotencyKey, orderId, amount, card, Ct);

    [Activity]
    public Task RefundAsync(string chargeKey) => payments.RefundAsync(chargeKey, Ct);

    [Activity]
    public Task<long> ReserveAsync(string idempotencyKey, long orderId, string sku, int quantity) =>
        inventory.ReserveAsync(idempotencyKey, orderId, sku, quantity, Ct);

    [Activity]
    public Task ReleaseAsync(string reservationKey) => inventory.ReleaseAsync(reservationKey, Ct);

    [Activity]
    public Task<long> ShipAsync(string idempotencyKey, long orderId, string address) =>
        shipping.ShipAsync(idempotencyKey, orderId, address, Ct);

    // Records the outcome on the order row, so GET /orders/{id} and the audit see it.
    [Activity]
    public async Task SaveOrderAsync(long orderId, string status, string? reason, long? chargeId, long? reservationId, long? shipmentId)
    {
        var order = await orders.GetAsync(orderId, Ct) ?? throw new InvalidOperationException($"no order {orderId}");
        await orders.SaveAsync(order with { Status = status, Reason = reason, ChargeId = chargeId, ReservationId = reservationId, ShipmentId = shipmentId }, Ct);
    }
}
