namespace Contention;

// Stage 2. Some rules span more than one row: "at most per_buyer_limit tickets per buyer" counts
// rows in tickets, which no single UPDATE on sales can check. Take a lock that every purchase for
// the sale must queue for, then read, check and write while you hold it.
//
// The goal, which the tests check:
//   - 1,000 buyers at once for 100 tickets: exactly 100 sold, tickets = sales.sold = 100
//   - one buyer sending 20 requests at once gets exactly 2 tickets (the limit); the rest LimitReached
//   - 200 buyers sending 3 requests each at once: 100 tickets, nobody over the limit
//
// Call check.RunAsync(buyerId, null, ct) once, before your write. Where the check runs relative
// to the lock is part of the exercise: measure it.
//
// Tests: dotnet test --filter-namespace "*Stage2"
public sealed class LockingTicketSeller(Databases db, PurchaseCheck check) : ITicketSeller
{
    public Task<PurchaseResult> BuyAsync(long saleId, long buyerId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
