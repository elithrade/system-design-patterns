using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Contention;

// Stage 4. Ticketmaster-style holds: a buyer first holds a seat for HoldSeconds, then buys it.
// The hold lives in Redis under Key(seatId), with the buyer's id as the value and a TTL, so an
// abandoned hold frees itself.
//
// The goal, which the tests check:
//   HoldAsync
//     - NotFound for no such seat, Taken if it is sold
//     - of 200 buyers holding one seat at once, exactly one gets Held; the rest get HeldByOther
//     - Held carries ExpiresAt (now + HoldSeconds, from the injected TimeProvider)
//     - after HoldSeconds the hold is gone and someone else can take it
//   ReleaseAsync
//     - frees the seat only if buyerId holds it; returns whether it did
//   PurchaseAsync
//     - NotHeld unless buyerId holds the seat right now
//     - otherwise sells it (seats.TrySellAsync), releases the hold, and answers Bought(seatId);
//       Taken if the seat turned out to be sold already
//     - the holder clicking Buy 10 times at once buys the seat once
//
// redis.GetDatabase() gives you the commands. "Check the value, then delete" in two commands is a
// race; a Lua script (ScriptEvaluateAsync) runs atomically inside Redis.
// Call check.RunAsync(buyerId, seatId, ct) once in PurchaseAsync, before selling.
//
// Tests: dotnet test --filter-namespace "*Stage4"
public sealed class SeatHolds(
    IConnectionMultiplexer redis, SeatRepository seats, PurchaseCheck check, IOptions<SaleOptions> options, TimeProvider time)
{
    private readonly TimeSpan _hold = TimeSpan.FromSeconds(options.Value.HoldSeconds);
    private readonly string _prefix = options.Value.KeyPrefix;

    private RedisKey Key(long seatId) => $"{_prefix}hold:{seatId}";

    public Task<PurchaseResult> HoldAsync(long seatId, long buyerId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<bool> ReleaseAsync(long seatId, long buyerId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<PurchaseResult> PurchaseAsync(long seatId, long buyerId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
