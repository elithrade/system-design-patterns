namespace ScalingWrites;

// Failures the tests switch on. Nothing in normal runs touches it.
public sealed class Faults
{
    private int _failingWrites;

    // The next n calls to VideoRepository.AddViewsAsync throw before writing anything.
    public void FailNextWrites(int n) => Volatile.Write(ref _failingWrites, n);

    // Added before every read a partition serves (stage 3), to make a slow partition visible.
    public TimeSpan PartitionReadDelay { get; set; } = TimeSpan.Zero;

    internal void BeforeWrite()
    {
        while (true)
        {
            var left = Volatile.Read(ref _failingWrites);
            if (left <= 0)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref _failingWrites, left - 1, left) == left)
            {
                throw new InjectedFaultException();
            }
        }
    }
}

public sealed class InjectedFaultException() : Exception("injected write failure");
