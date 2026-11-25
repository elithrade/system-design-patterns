using System.Net.WebSockets;

namespace Realtime;

// Stage 3: WebSocket. GET /auctions/{id}/ws accepts the socket and calls RunAsync, which owns it
// until it closes. Messages are in Messages.cs.
//
//   - every new bid on the auction goes to the client as a BidMessage
//   - the client can place bids over the socket (PlaceBidMessage); answer each with a
//     ResultMessage carrying its ref, whatever the outcome (bids.PlaceAsync)
//   - a message you cannot parse gets an ErrorMessage; the socket stays open
//   - when the client closes, complete the close handshake, stop, and release the subscription;
//     the same when ct is cancelled
//   - call stats.Sent(bid) for every bid you send
// Bids arrive from the bus while you are reading the client's messages, so two things want to
// send at once. WebSocket's documentation allows one SendAsync at a time. .NET's implementation
// happens to queue a second one; don't rely on that (other WebSocket libraries interleave or throw).
//
// Serialize with Json.Options. One message per text frame (endOfMessage: true).
//
// Tests: dotnet test --filter-namespace "*Stage3"
public sealed class BidSocket(BidService bids, IBidBus bus, Stats stats)
{
    public Task RunAsync(WebSocket socket, long auctionId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
