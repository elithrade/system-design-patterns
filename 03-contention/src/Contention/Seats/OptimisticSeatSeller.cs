using Microsoft.Extensions.Options;

namespace Contention;

// Stage 3. Optimistic concurrency: read the seat with its version, do the check without holding
// any lock, then write only if the version is still the one you read. If it is not, someone
// changed the seat in between: count it with stats.Conflict() and start over, up to MaxAttempts
// attempts in all, then answer Busy.
//
// Write a migration, db/migrations/002_<name>.sql, that adds `version int NOT NULL DEFAULT 0`
// to seats (the tests use that name). Every write to a seat must move its version on.
//
// The goal, which the tests check:
//   - 300 buyers at once for 100 seats: no seat is sold twice, and every Bought names a different seat
//   - one buyer at a time gets seats 1, 2, 3 ... in order, then SoldOut
//   - if the seat's version changes between your read and your write (the tests change it inside
//     check.RunAsync), you retry and still sell a seat, and stats.Conflicts is 1
//   - if it changes every time, you answer Busy after MaxAttempts attempts and sell nothing
//
// Call check.RunAsync(buyerId, seatId, ct) once per attempt, between the read and the write.
//
// Tests: dotnet test --filter-namespace "*Stage3"
public sealed class OptimisticSeatSeller(Databases db, PurchaseCheck check, Stats stats, IOptions<SaleOptions> options)
    : IBestSeatSeller
{
    private readonly int _maxAttempts = options.Value.MaxAttempts;

    public Task<PurchaseResult> BuyBestAsync(long saleId, long buyerId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
