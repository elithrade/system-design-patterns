using Microsoft.Extensions.Options;

namespace MultiStep;

// Stage 2: a saga. Each step has an undo: refund the charge, release the reservation. Shipping has
// no undo, so it goes last. RunAsync creates (or finds) the order and calls ResumeAsync.
//
// ResumeAsync drives an order from whatever state it is in to completed or cancelled:
//   - a finished order: return its result, call nothing
//   - status running: do the steps not done yet (ChargeId, ReservationId, ShipmentId tell you
//     which), saving each id on the order as soon as you have it (orders.SaveAsync)
//   - a transient failure (ServiceUnavailableException, LostResponseException): retry the step,
//     up to MaxAttempts calls, waiting RetryMilliseconds * 2^(n-1) between them. Use the
//     order's key for the step (order.KeyFor) so a retry never does the work twice
//   - a permanent failure (PermanentFailureException), or a step out of attempts: save status
//     compensating with the reason, then undo the steps that were done, newest first, and save
//     cancelled. A step whose last attempt lost its response may have happened without you
//     knowing its id: undo by the step's key (RefundAsync and ReleaseAsync take it; undoing
//     something that never happened does nothing). An undo that fails transiently is retried
//     until it succeeds: giving up would leave a charge for an order that will never ship
//   - never catch ProcessCrashException (see Faults.cs)
// Stage 3 resumes orders after a crash with this same method, so an order that is compensating
// must keep compensating when resumed.
//
// Tests: dotnet test --filter-namespace "*Stage2"
public sealed class SagaCheckout(Orders orders, Payments payments, Inventory inventory, Shipping shipping, IOptions<CheckoutOptions> options) : ICheckout
{
    private readonly int _maxAttempts = options.Value.MaxAttempts;
    private readonly TimeSpan _retry = TimeSpan.FromMilliseconds(options.Value.RetryMilliseconds);

    public async Task<CheckoutResult> RunAsync(CheckoutRequest request, string opKey, CancellationToken ct)
    {
        var order = await orders.CreateAsync(request, opKey, ct);
        return await ResumeAsync(order, ct);
    }

    public Task<CheckoutResult> ResumeAsync(Order order, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
