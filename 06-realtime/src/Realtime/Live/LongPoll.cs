using Microsoft.Extensions.Options;

namespace Realtime;

// Stage 1: long polling. GET /auctions/{id}/bids?after=41&waitSeconds=25 calls WaitAsync.
//
// Return the auction's bids after afterId:
//   - if there are some already, return them at once
//   - otherwise wait until a new bid arrives, and return the new bids
//   - if wait passes first (measured on the injected TimeProvider), return an empty list
//   - if ct is cancelled (the client went away), stop waiting; returning or throwing are both fine
// A bid committed after you read the database but before you start waiting must not be missed:
// the test places one at exactly that moment. Think about when to subscribe.
// Release your subscription however you leave.
//
// auctions.AfterAsync reads the database; bus.SubscribeAsync hears new bids (IBidBus.cs).
// The endpoint caps wait at MaxWaitSeconds and counts the request.
//
// Tests: dotnet test --filter-namespace "*Stage1"
public sealed class LongPoll(Auctions auctions, IBidBus bus, TimeProvider time)
{
    public Task<IReadOnlyList<BidEvent>> WaitAsync(long auctionId, long afterId, TimeSpan wait, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
