using System.Text.Json;
using System.Text.Json.Serialization;

namespace Realtime;

// JSON as every part of this lab writes it: camelCase, enums as strings.
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

// WebSocket messages (stage 3), one JSON object per text message.
//
// client -> server   {"type": "bid", "ref": "a1", "bidderId": 7, "amount": 150}
// server -> client   {"type": "bid", "bid": {"id": 12, "auctionId": 1, "bidderId": 7, "amount": 150, "placedAt": "..."}}
//                    {"type": "result", "ref": "a1", "outcome": "Accepted", "currentPrice": 150}
//                    {"type": "error", "message": "..."}
public sealed record PlaceBidMessage(string Type, string? Ref, long BidderId, long Amount);

public sealed record BidMessage(BidEvent Bid)
{
    public string Type => "bid";
}

public sealed record ResultMessage(string? Ref, BidOutcome Outcome, long CurrentPrice)
{
    public string Type => "result";
}

public sealed record ErrorMessage(string Message)
{
    public string Type => "error";
}
