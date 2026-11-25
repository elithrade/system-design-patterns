namespace Contention;

public enum Outcome
{
    // 201: a ticket or seat is yours; Id is the ticket or seat id
    Bought,
    // 200: the seat is held for you until ExpiresAt; Id is the seat id
    Held,
    // 409: no tickets or seats left
    SoldOut,
    // 409: this buyer already has per_buyer_limit tickets
    LimitReached,
    // 409: that seat is sold
    Taken,
    // 409: someone else holds that seat
    HeldByOther,
    // 409: you tried to buy a seat you do not hold
    NotHeld,
    // 503: gave up after conflicts; try again
    Busy,
    // 404: no such sale or seat
    NotFound,
}

public sealed record PurchaseResult(Outcome Outcome, long? Id = null, DateTimeOffset? ExpiresAt = null)
{
    public static PurchaseResult Of(Outcome outcome) => new(outcome);
    public static PurchaseResult Bought(long id) => new(Outcome.Bought, id);
}
