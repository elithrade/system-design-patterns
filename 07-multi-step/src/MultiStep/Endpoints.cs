using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiStep;

public static class Endpoints
{
    public const string KeyHeader = "Idempotency-Key";

    public static void MapEndpoints(this WebApplication app)
    {
        // Runs the checkout with the engine Checkout:Engine picks. With an Idempotency-Key header,
        // through your IdempotencyStore (stage 1).
        app.MapPost("/checkout", async (CheckoutRequest request, HttpContext context, ICheckout checkout, IdempotencyStore store,
            ILogger<CheckoutRequest> logger, CancellationToken ct) =>
        {
            string? key = context.Request.Headers[KeyHeader];
            try
            {
                var response = string.IsNullOrEmpty(key)
                    ? await HandleAsync(checkout, request, Guid.NewGuid().ToString("N"), ct)
                    : await store.RunAsync(key, Fingerprint(request), token => HandleAsync(checkout, request, key, token), ct);
                return Results.Text(response.Body, "application/json", statusCode: response.StatusCode);
            }
            catch (Exception e) when (e is ServiceUnavailableException or LostResponseException)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Checkout failed: {Error}", e.Message);
                return Results.Problem(e.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/orders/{id:long}", async (long id, Orders orders, CancellationToken ct) =>
            await orders.GetAsync(id, ct) is { } order ? Results.Ok(order) : Results.NotFound());

        app.MapGet("/audit", async (Audit audit, CancellationToken ct) => Results.Ok(await audit.ReportAsync(ct)));

        // Stage 3: run recovery once now.
        app.MapPost("/admin/recover", async (SagaRecovery recovery, CancellationToken ct) =>
            Results.Ok(new { resumed = await recovery.RecoverAsync(ct) }));
    }

    // A decided outcome becomes a response (and, with a key, is stored). Transient failures and
    // crashes throw: nothing was decided.
    private static async Task<StoredResponse> HandleAsync(ICheckout checkout, CheckoutRequest request, string opKey, CancellationToken ct)
    {
        try
        {
            var result = await checkout.RunAsync(request, opKey, ct);
            var status = result.Status == OrderStatus.Completed ? StatusCodes.Status201Created : StatusCodes.Status409Conflict;
            return new(status, JsonSerializer.Serialize(result, JsonSerializerOptions.Web));
        }
        catch (PermanentFailureException e)
        {
            // the baseline: it stops at the first permanent failure, without undoing anything
            return new(StatusCodes.Status409Conflict, JsonSerializer.Serialize(new { status = OrderStatus.Failed, reason = e.Message }, JsonSerializerOptions.Web));
        }
    }

    private static string Fingerprint(CheckoutRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
}
