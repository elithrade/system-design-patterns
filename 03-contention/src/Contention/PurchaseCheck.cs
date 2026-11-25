using Microsoft.Extensions.Options;

namespace Contention;

// The work a real purchase does between deciding to sell and writing the sale: a fraud check, a
// price lookup, a call to another service. Here it waits CheckMilliseconds (real time).
//
// Every seller calls RunAsync once per attempt, AFTER it has read what it needs and BEFORE it writes.
// That gap is where concurrent buyers overtake each other.
public sealed class PurchaseCheck(IOptions<SaleOptions> options)
{
    private readonly int _ms = options.Value.CheckMilliseconds;

    // The tests set this to act inside the gap, e.g. to change the seat being bought.
    // Arguments: buyerId, seatId (null for general-admission tickets).
    public Func<long, long?, Task>? Hook { get; set; }

    public async Task RunAsync(long buyerId, long? seatId, CancellationToken ct)
    {
        if (_ms > 0)
        {
            await Task.Delay(_ms, ct);
        }
        if (Hook is { } hook)
        {
            await hook(buyerId, seatId);
        }
    }
}
