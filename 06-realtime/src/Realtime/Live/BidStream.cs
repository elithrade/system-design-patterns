using Microsoft.Extensions.Options;

namespace Realtime;

// Stages 2 and 5: Server-Sent Events. GET /auctions/{id}/stream calls RunAsync and keeps the
// response open until ct is cancelled (the client left, or ?seconds= ran out).
//
// Stage 2: a live stream.
//   - headers: Content-Type text/event-stream, Cache-Control no-cache. Send them at once, before
//     any bid: a client waiting for headers sees nothing until you flush
//   - each new bid on the auction as one event, flushed when written:
//         id: <bid id>
//         event: bid
//         data: <the BidEvent as JSON, Json.Options>
//         (blank line)
//   - a comment line (": ping" then a blank line) every HeartbeatSeconds, on the injected
//     TimeProvider (PeriodicTimer takes one)
//   - call stats.Sent(bid) for every bid you write (it measures delivery latency for k6)
//   - when ct is cancelled, return normally and release the subscription
// A fresh connection (lastEventId null) starts with bids placed after it connects.
//
// Stage 5: resume. EventSource reconnects by itself and sends the last id it saw in the
// Last-Event-ID header; the endpoint passes it as lastEventId. Send every bid after it, then
// continue live, with no gap and no duplicate. The tests place a bid just before and just after
// your database read. There may be more missed bids than one AfterAsync page.
//
// Tests: dotnet test --filter-namespace "*Stage2" (then Stage5)
public sealed class BidStream(Auctions auctions, IBidBus bus, Stats stats, TimeProvider time, IOptions<RealtimeOptions> options)
{
    private readonly TimeSpan _heartbeat = TimeSpan.FromSeconds(options.Value.HeartbeatSeconds);

    public Task RunAsync(HttpResponse response, long auctionId, long? lastEventId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
