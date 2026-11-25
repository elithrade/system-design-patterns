using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Realtime;

// A bid as watchers see it. Id orders bids within an auction: "the bids after 41".
public sealed record BidEvent(long Id, long AuctionId, long BidderId, long Amount, DateTime PlacedAt);

public sealed record Auction(long Id, string Title, DateTime EndsAt, long CurrentPrice, long? LeaderId);

public enum BidOutcome { Accepted, TooLow, Ended, NotFound }

public sealed record BidResult(BidOutcome Outcome, BidEvent? Bid, long CurrentPrice);

// Hooks the tests use to place a bid at an awkward moment: just before or just after AfterAsync
// reads. Each runs once, then clears itself. Null outside tests.
public sealed class ReadHooks
{
    private Func<Task>? _beforeRead, _afterRead;

    public void BeforeNextRead(Func<Task> hook) => _beforeRead = hook;
    public void AfterNextRead(Func<Task> hook) => _afterRead = hook;

    internal Task BeforeReadAsync() => Interlocked.Exchange(ref _beforeRead, null)?.Invoke() ?? Task.CompletedTask;
    internal Task AfterReadAsync() => Interlocked.Exchange(ref _afterRead, null)?.Invoke() ?? Task.CompletedTask;
}

public sealed class Auctions(NpgsqlDataSource db, ReadHooks hooks, IOptions<RealtimeOptions> options)
{
    public int PageSize => options.Value.PageSize;

    public async Task<Auction?> GetAsync(long auctionId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Auction>(new CommandDefinition(
            "SELECT id, title, ends_at, current_price, leader_id FROM auctions WHERE id = @auctionId", new { auctionId }, cancellationToken: ct));
    }

    // The auction's bids with id > afterId, oldest first, at most PageSize of them.
    public async Task<IReadOnlyList<BidEvent>> AfterAsync(long auctionId, long afterId, CancellationToken ct)
    {
        await hooks.BeforeReadAsync();
        IReadOnlyList<BidEvent> bids;
        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            bids = (await conn.QueryAsync<BidEvent>(new CommandDefinition("""
                SELECT id, auction_id, bidder_id, amount, placed_at FROM bids
                WHERE auction_id = @auctionId AND id > @afterId ORDER BY id LIMIT @limit
                """, new { auctionId, afterId, limit = PageSize }, cancellationToken: ct))).AsList();
        }
        await hooks.AfterReadAsync();
        return bids;
    }
}

// Places bids and tells the bus. A bid must beat the current price on an auction that has not ended.
public sealed class BidService(NpgsqlDataSource db, IBidBus bus, TimeProvider time)
{
    public async Task<BidResult> PlaceAsync(long auctionId, long bidderId, long amount, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        BidEvent? bid;
        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            // one statement: the conditional UPDATE locks the auction row, so concurrent bids queue
            // behind it and bid ids increase in the order the prices rose
            bid = await conn.QuerySingleOrDefaultAsync<BidEvent>(new CommandDefinition("""
                WITH raised AS (
                    UPDATE auctions SET current_price = @amount, leader_id = @bidderId
                    WHERE id = @auctionId AND current_price < @amount AND ends_at > @now
                    RETURNING id)
                INSERT INTO bids (auction_id, bidder_id, amount, placed_at)
                SELECT id, @bidderId, @amount, @now FROM raised
                RETURNING id, auction_id, bidder_id, amount, placed_at
                """, new { auctionId, bidderId, amount, now }, cancellationToken: ct));
            if (bid is null)
            {
                var auction = await conn.QuerySingleOrDefaultAsync<(DateTime EndsAt, long CurrentPrice)?>(new CommandDefinition(
                    "SELECT ends_at, current_price FROM auctions WHERE id = @auctionId", new { auctionId }, cancellationToken: ct));
                return auction switch
                {
                    null => new(BidOutcome.NotFound, null, 0),
                    var (endsAt, price) when endsAt <= now.UtcDateTime => new(BidOutcome.Ended, null, price),
                    var (_, price) => new(BidOutcome.TooLow, null, price),
                };
            }
        }
        // after the commit: a watcher who hears about the bid can read it
        await bus.PublishAsync(bid, ct);
        return new(BidOutcome.Accepted, bid, bid.Amount);
    }
}
