namespace MultiStep;

// One checkout: charge the card, reserve the stock, create the shipment. opKey identifies the
// attempt end to end; running it again with the same opKey continues the same order.
public interface ICheckout
{
    Task<CheckoutResult> RunAsync(CheckoutRequest request, string opKey, CancellationToken ct);
}

// The baseline: the three calls in a row. The first failure stops it: the order is 'failed' and
// whatever already happened stays happened (a charge without stock, stock held forever).
public sealed class NaiveCheckout(Orders orders, Payments payments, Inventory inventory, Shipping shipping) : ICheckout
{
    public async Task<CheckoutResult> RunAsync(CheckoutRequest request, string opKey, CancellationToken ct)
    {
        var order = await orders.CreateAsync(request, opKey, ct);
        try
        {
            var chargeId = await payments.ChargeAsync(order.KeyFor(Op.Charge), order.Id, order.Amount, order.Card, ct);
            order = await orders.SaveAsync(order with { ChargeId = chargeId }, ct);
            var reservationId = await inventory.ReserveAsync(order.KeyFor(Op.Reserve), order.Id, order.Sku, order.Quantity, ct);
            order = await orders.SaveAsync(order with { ReservationId = reservationId }, ct);
            var shipmentId = await shipping.ShipAsync(order.KeyFor(Op.Ship), order.Id, order.Address, ct);
            order = await orders.SaveAsync(order with { ShipmentId = shipmentId, Status = OrderStatus.Completed, Reason = null }, ct);
            return order.Result;
        }
        catch (Exception e) when (e is not ProcessCrashException)
        {
            await orders.SaveAsync(order with { Status = OrderStatus.Failed, Reason = e.Message }, CancellationToken.None);
            throw;
        }
    }
}
