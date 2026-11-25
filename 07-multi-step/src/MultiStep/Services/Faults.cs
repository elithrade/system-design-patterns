using Microsoft.Extensions.Options;

namespace MultiStep;

public enum Op { Charge, Refund, Reserve, Release, Ship }

// Retrying may help. The operation did not happen.
public sealed class ServiceUnavailableException(Op op) : Exception($"{op}: service unavailable");

// Retrying may help. The operation may or may not have happened: the response was lost.
public sealed class LostResponseException(Op op) : Exception($"{op}: no response (it may have happened)");

// Retrying will not help.
public abstract class PermanentFailureException(string message) : Exception(message);
public sealed class PaymentDeclinedException() : PermanentFailureException("the card was declined");
public sealed class OutOfStockException(string sku) : PermanentFailureException($"{sku} is out of stock");
public sealed class UndeliverableException() : PermanentFailureException("we cannot ship to that address");

// The process died right here. Tests throw it to simulate a crash: nothing after this point runs.
// Never catch it, retry it, or compensate for it; let it go up.
public sealed class ProcessCrashException(Op op) : Exception($"the process died right after {op}");

// Failures the tests plan and the load test samples. Every service call goes through BeforeAsync
// and AfterAsync.
public sealed class Faults(IOptions<FaultOptions> options)
{
    private readonly FaultOptions _options = options.Value;
    private readonly Dictionary<Op, Queue<Type>> _planned = [];
    private readonly HashSet<Op> _crashAfter = [], _hangAfter = [];
    private readonly Dictionary<Op, int> _calls = [];

    // The next `times` calls of op fail with ServiceUnavailableException or LostResponseException.
    public void Fail<TException>(Op op, int times = 1) where TException : Exception
    {
        lock (_planned)
        {
            if (!_planned.TryGetValue(op, out var queue))
            {
                _planned[op] = queue = new();
            }
            for (var i = 0; i < times; i++)
            {
                queue.Enqueue(typeof(TException));
            }
        }
    }

    // The next call of op does its work, then throws ProcessCrashException.
    public void CrashAfter(Op op)
    {
        lock (_planned)
        {
            _crashAfter.Add(op);
        }
    }

    // The next call of op does its work, then hangs until cancelled (stage 4: a worker dies mid-call).
    public void HangAfter(Op op)
    {
        lock (_planned)
        {
            _hangAfter.Add(op);
        }
    }

    // Calls of op so far, including failed ones.
    public int Calls(Op op)
    {
        lock (_planned)
        {
            return _calls.GetValueOrDefault(op);
        }
    }

    internal async Task BeforeAsync(Op op, CancellationToken ct)
    {
        if (_options.LatencyMs > 0)
        {
            await Task.Delay(_options.LatencyMs, ct);
        }
        lock (_planned)
        {
            _calls[op] = _calls.GetValueOrDefault(op) + 1;
            if (_planned.TryGetValue(op, out var queue) && queue.TryPeek(out var next) && next == typeof(ServiceUnavailableException))
            {
                queue.Dequeue();
                throw new ServiceUnavailableException(op);
            }
        }
        if (Random.Shared.NextDouble() < _options.TransientRate)
        {
            throw new ServiceUnavailableException(op);
        }
    }

    internal async Task AfterAsync(Op op, CancellationToken ct)
    {
        bool hang;
        lock (_planned)
        {
            if (_crashAfter.Remove(op))
            {
                throw new ProcessCrashException(op);
            }
            hang = _hangAfter.Remove(op);
            if (!hang && _planned.TryGetValue(op, out var queue) && queue.TryPeek(out var next) && next == typeof(LostResponseException))
            {
                queue.Dequeue();
                throw new LostResponseException(op);
            }
        }
        if (hang)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        if (Random.Shared.NextDouble() < _options.LostResponseRate)
        {
            throw new LostResponseException(op);
        }
    }
}
