namespace Contention;

// Stage 1. Make the database decide: the capacity check and the increment happen in ONE statement,
// so two buyers can never both take the last ticket. Insert the ticket in the same transaction.
//
// The goal, which the tests check:
//   - 1,000 buyers at once for 100 tickets: exactly 100 get Bought, the rest SoldOut, and
//     tickets and sales.sold both say 100
//   - one buyer at a time: the 3rd ticket for the same buyer is LimitReached (with the default
//     limit of 2); a missing sale is NotFound
//
// Call check.RunAsync(buyerId, null, ct) once, before your write.
// Stage 2 is about the per-buyer limit under concurrency; here it only has to hold one at a time.
//
// Tests: dotnet test --filter-namespace "*Stage1"
public sealed class ConditionalTicketSeller(Databases db, PurchaseCheck check) : ITicketSeller
{
    public Task<PurchaseResult> BuyAsync(long saleId, long buyerId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
