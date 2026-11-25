namespace Realtime;

public enum BusKind
{
    // in-process: a bid reaches watchers connected to the same app instance only
    Local,
    // stage 4: Redis pub/sub, so every instance hears every bid
    Redis,
}

// The "Realtime" section. Set from the command line with Realtime__Bus=Redis etc.
public sealed class RealtimeOptions
{
    public const string Section = "Realtime";

    public BusKind Bus { get; set; } = BusKind.Local;

    // Stage 1: the longest a long poll may wait, whatever the client asks for.
    public int MaxWaitSeconds { get; set; } = 30;

    // Stage 2: an idle stream sends a comment this often, so proxies and clients see it is alive.
    public int HeartbeatSeconds { get; set; } = 15;

    // Most bids one read returns (Auctions.AfterAsync).
    public int PageSize { get; set; } = 100;

    // Stage 4: Redis channels are named {ChannelPrefix}auction:{auctionId}.
    public string ChannelPrefix { get; set; } = "lab6:";

    // Shown in GET /stats, to tell two instances apart.
    public string Instance { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";
}
