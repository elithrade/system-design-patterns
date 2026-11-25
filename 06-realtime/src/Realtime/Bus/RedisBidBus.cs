using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Realtime;

// Stage 4: a bus that spans app instances, on Redis pub/sub. Realtime:Bus=Redis selects it.
//
//   - PublishAsync sends the bid (JSON, Json.Options) to channel {ChannelPrefix}auction:{auctionId}
//   - SubscribeAsync returns a Subscription (IBidBus.cs) fed from that channel, live when the task
//     completes; Deliver hands it a bid
//   - every subscriber gets each bid once, including bids published by this same instance
//   - disposing a subscription stops it, and only it: another subscriber to the same auction on
//     this instance keeps receiving. Once nobody on this instance listens to an auction, this
//     instance must no longer be subscribed to its channel in Redis
//
// redis.GetSubscriber() gives the pub/sub API. Use RedisChannel.Literal for channel names.
//
// Tests: dotnet test --filter-namespace "*Stage4"
public sealed class RedisBidBus(IConnectionMultiplexer redis, Stats stats, IOptions<RealtimeOptions> options) : IBidBus
{
    private readonly string _prefix = options.Value.ChannelPrefix;

    public Task PublishAsync(BidEvent bid, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<IBidSubscription> SubscribeAsync(long auctionId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
