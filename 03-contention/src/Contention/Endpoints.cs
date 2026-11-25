namespace Contention;

public static class Endpoints
{
    // There is no auth. The caller says who they are in this header.
    public const string BuyerHeader = "X-Buyer-Id";

    public static void MapEndpoints(this WebApplication app)
    {
        // Whether the sale kept its promises: capacity, tickets, seats sold twice.
        app.MapGet("/sales/{id:long}", async (long id, Audit audit, CancellationToken ct) =>
            await audit.SaleAsync(id, ct) is { } a ? Results.Ok(a) : Results.NotFound());

        // One general-admission ticket (stages 1 and 2).
        app.MapPost("/sales/{id:long}/tickets", (long id, HttpContext http, ITicketSeller seller, Stats stats, CancellationToken ct) =>
            Answer(http, stats, buyer => seller.BuyAsync(id, buyer, ct)));

        // The best available seat (stage 3).
        app.MapPost("/sales/{id:long}/seats/best", (long id, HttpContext http, IBestSeatSeller seller, Stats stats, CancellationToken ct) =>
            Answer(http, stats, buyer => seller.BuyBestAsync(id, buyer, ct), seats: http.RequestServices.GetRequiredService<SeatRepository>()));

        app.MapGet("/seats/{id:long}", async (long id, SeatRepository seats, CancellationToken ct) =>
            await seats.GetAsync(id, ct) is { } seat ? Results.Ok(seat) : Results.NotFound());

        // Holds (stage 4). 501 unless Sale:Seats is Held.
        app.MapPost("/seats/{id:long}/hold", (long id, HttpContext http, Stats stats, CancellationToken ct) =>
            Holds(http) is { } holds
                ? Answer(http, stats, buyer => holds.HoldAsync(id, buyer, ct))
                : Task.FromResult(NoHolds()));

        app.MapDelete("/seats/{id:long}/hold", async (long id, HttpContext http, CancellationToken ct) =>
        {
            if (Holds(http) is not { } holds)
            {
                return NoHolds();
            }
            if (BuyerId(http) is not { } buyer)
            {
                return NoBuyer();
            }
            return await holds.ReleaseAsync(id, buyer, ct) ? Results.NoContent() : Results.Conflict();
        });

        // Buy a specific seat: through its hold under stage 4, read-check-write otherwise.
        app.MapPost("/seats/{id:long}/purchase", (long id, HttpContext http, Stats stats, SeatRepository seats, CancellationToken ct) =>
            Answer(http, stats, buyer => Holds(http) is { } holds
                ? holds.PurchaseAsync(id, buyer, ct)
                : http.RequestServices.GetRequiredService<NaiveSeatSeller>().BuySeatAsync(id, buyer, ct),
                seats));

        // Whether the sales and payments databases agree (the README's last section).
        app.MapGet("/payments/audit", async (Databases db, Audit audit, CancellationToken ct) =>
            db.Payments is null ? Results.Problem("ConnectionStrings:Payments is not set", statusCode: 501) : Results.Ok(await audit.PaymentsAsync(ct)));

        app.MapGet("/stats", (Stats stats) => Results.Ok(stats.Snapshot()));
    }

    private static async Task<IResult> Answer(
        HttpContext http, Stats stats, Func<long, Task<PurchaseResult>> act, SeatRepository? seats = null)
    {
        if (BuyerId(http) is not { } buyer)
        {
            return NoBuyer();
        }
        PurchaseResult result;
        try
        {
            result = await act(buyer);
        }
        catch (SimulatedCrashException)
        {
            return Results.Problem("crashed after the payment", statusCode: 500);
        }
        stats.Answered(result.Outcome);
        if (result.Outcome == Outcome.Bought && seats is not null)
        {
            await seats.RecordOrderAsync(result.Id!.Value, buyer, http.RequestAborted);
        }
        var status = result.Outcome switch
        {
            Outcome.Bought => StatusCodes.Status201Created,
            Outcome.Held => StatusCodes.Status200OK,
            Outcome.Busy => StatusCodes.Status503ServiceUnavailable,
            Outcome.NotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict,
        };
        return Results.Json(result, statusCode: status);
    }

    private static SeatHolds? Holds(HttpContext http) => http.RequestServices.GetService<SeatHolds>();

    private static IResult NoHolds() => Results.Problem("holds need Sale:Seats=Held", statusCode: 501);

    private static IResult NoBuyer() => Results.Problem($"{BuyerHeader} header is required", statusCode: 401);

    private static long? BuyerId(HttpContext http) =>
        long.TryParse(http.Request.Headers[BuyerHeader], out var id) ? id : null;
}
