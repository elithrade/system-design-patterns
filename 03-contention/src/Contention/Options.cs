namespace Contention;

// How POST /sales/{id}/tickets sells a general-admission ticket.
public enum TicketStrategy
{
    // read, check, write (the baseline): oversells
    Naive,
    // stage 1: one conditional UPDATE
    Conditional,
    // stage 2: SELECT ... FOR UPDATE, then check and write
    Locking,
    // the README's last section: tickets in one database, payments in another
    TwoDatabases,
}

// How the seat endpoints sell a reserved seat.
public enum SeatStrategy
{
    // read, check, write (the baseline): sells a seat twice
    Naive,
    // stage 3: POST /sales/{id}/seats/best with a version column and retries
    Optimistic,
    // stage 4: hold a seat in Redis, then buy it
    Held,
}

// The "Sale" configuration section. Set from the command line with Sale__Tickets=Locking etc.
public sealed class SaleOptions
{
    public const string Section = "Sale";

    public TicketStrategy Tickets { get; set; } = TicketStrategy.Naive;
    public SeatStrategy Seats { get; set; } = SeatStrategy.Naive;

    // How long PurchaseCheck takes: the work between deciding to sell and writing the sale.
    public int CheckMilliseconds { get; set; } = 5;

    // Stage 3: attempts before an optimistic purchase gives up with Busy.
    public int MaxAttempts { get; set; } = 3;

    // Stage 4: how long a hold lasts.
    public int HoldSeconds { get; set; } = 60;

    // Prepended to every Redis key. The tests give each test its own prefix.
    public string KeyPrefix { get; set; } = "sale:";

    // TwoDatabases: the share of purchases that die after the payment is written and before the
    // ticket commits.
    public double CrashAfterPaymentRate { get; set; }
}
