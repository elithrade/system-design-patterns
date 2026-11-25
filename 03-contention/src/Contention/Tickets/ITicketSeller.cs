namespace Contention;

// Sells one general-admission ticket of a sale to a buyer. Sale:Tickets picks the implementation.
//
// Every implementation answers:
//   NotFound      no such sale
//   SoldOut       sold has reached capacity
//   LimitReached  the buyer already holds per_buyer_limit tickets for this sale
//   Bought(id)    otherwise: a row in tickets (its id) and sales.sold one higher, together
// and calls check.RunAsync(buyerId, null, ct) once before writing (see PurchaseCheck).
public interface ITicketSeller
{
    Task<PurchaseResult> BuyAsync(long saleId, long buyerId, CancellationToken ct);
}
