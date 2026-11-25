using Microsoft.Extensions.Options;

namespace Realtime;

public sealed record PlaceBid(long BidderId, long Amount);

public static class Endpoints
{
    public static void MapEndpoints(this WebApplication app)
    {
        app.MapGet("/auctions/{id:long}", async (long id, Auctions auctions, CancellationToken ct) =>
            await auctions.GetAsync(id, ct) is { } auction ? Results.Ok(auction) : Results.NotFound());

        app.MapPost("/auctions/{id:long}/bids", async (long id, PlaceBid bid, BidService bids, CancellationToken ct) =>
        {
            var result = await bids.PlaceAsync(id, bid.BidderId, bid.Amount, ct);
            return result.Outcome switch
            {
                BidOutcome.Accepted => Results.Created($"/auctions/{id}/bids/{result.Bid!.Id}", result.Bid),
                BidOutcome.NotFound => Results.NotFound(),
                _ => Results.Conflict(result),
            };
        });

        // The baseline is polling: ask "anything after 41?" and get an answer at once.
        // With waitSeconds, a long poll (stage 1).
        app.MapGet("/auctions/{id:long}/bids", async (long id, long? after, int? waitSeconds, Auctions auctions, LongPoll longPoll,
            Stats stats, IOptions<RealtimeOptions> options, CancellationToken ct) =>
        {
            IReadOnlyList<BidEvent> bids;
            if (waitSeconds is null or <= 0)
            {
                stats.Poll();
                bids = await auctions.AfterAsync(id, after ?? 0, ct);
            }
            else
            {
                stats.LongPoll();
                stats.Opened();
                try
                {
                    var wait = TimeSpan.FromSeconds(Math.Min(waitSeconds.Value, options.Value.MaxWaitSeconds));
                    bids = await longPoll.WaitAsync(id, after ?? 0, wait, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return Results.Empty;
                }
                finally
                {
                    stats.Closed();
                }
            }
            foreach (var bid in bids)
            {
                stats.Sent(bid);
            }
            return Results.Ok(bids);
        });

        // Server-Sent Events (stages 2 and 5). ?seconds=N closes the stream after N seconds (k6
        // uses it); ?after=N resumes like a Last-Event-ID header.
        app.MapGet("/auctions/{id:long}/stream", async (long id, long? after, int? seconds, HttpContext context, BidStream stream, Stats stats) =>
        {
            long? lastEventId = long.TryParse(context.Request.Headers["Last-Event-ID"], out var header) ? header : after;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            if (seconds is > 0)
            {
                cts.CancelAfter(TimeSpan.FromSeconds(seconds.Value));
            }
            stats.Stream();
            stats.Opened();
            try
            {
                await stream.RunAsync(context.Response, id, lastEventId, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            finally
            {
                stats.Closed();
            }
        });

        // WebSocket (stage 3).
        app.Map("/auctions/{id:long}/ws", async (long id, HttpContext context, BidSocket sockets, Stats stats) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            stats.Socket();
            stats.Opened();
            try
            {
                await sockets.RunAsync(socket, id, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            finally
            {
                stats.Closed();
            }
        });

        app.MapGet("/stats", (Stats stats) => Results.Ok(stats.Snapshot()));
        app.MapPost("/stats/reset", (Stats stats) =>
        {
            stats.Reset();
            return Results.Ok(stats.Snapshot());
        });
    }
}
