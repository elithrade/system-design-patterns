namespace MultiStep;

public enum Engine
{
    // the baseline: one step after another, give up on the first error
    Naive,
    // stages 2-3: your saga
    Saga,
    // stage 4: your Temporal workflow
    Temporal,
}

// The "Checkout" section. Set from the command line with Checkout__Engine=Saga etc.
public sealed class CheckoutOptions
{
    public const string Section = "Checkout";

    public Engine Engine { get; set; } = Engine.Naive;

    // Stages 2 and 4: attempts per step before a transient failure counts as permanent.
    public int MaxAttempts { get; set; } = 5;

    // Stages 2 and 4: the wait before retry n is RetryMilliseconds * 2^(n-1). Real time.
    public int RetryMilliseconds { get; set; } = 50;

    // Stage 3: an order's lease. Recovery leaves an order alone until its lease has passed.
    public int LeaseSeconds { get; set; } = 30;

    // Stage 3: how often the recovery loop looks for abandoned orders. 0: never (the tests).
    public int RecoverySeconds { get; set; } = 5;

    // Stage 1: how long a key stays locked by a request that never finished.
    public int KeyLockSeconds { get; set; } = 60;
}

// The "Faults" section: failures injected into every service call, for load tests.
public sealed class FaultOptions
{
    public const string Section = "Faults";

    // Chance a call fails before doing anything (ServiceUnavailableException).
    public double TransientRate { get; set; }

    // Chance a call does its work, then the response is lost (LostResponseException).
    public double LostResponseRate { get; set; }

    // Added to every call.
    public int LatencyMs { get; set; }
}

// The "Temporal" section (stage 4).
public sealed class TemporalOptions
{
    public const string Section = "Temporal";

    public string Address { get; set; } = "localhost:57233";
    public string Namespace { get; set; } = "default";
    public string TaskQueue { get; set; } = "checkout";

    // An activity attempt that takes longer than this has failed (its worker may be dead).
    public int ActivityTimeoutSeconds { get; set; } = 10;
}
