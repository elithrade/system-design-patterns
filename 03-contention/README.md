# Lab 3: dealing with contention

A flash sale: 100 tickets, 100 reserved seats, 5,000 buyers who all click Buy in the same second. The baseline reads the sale, decides, runs a purchase check, then writes, and it is correct for one buyer at a time. Under the burst it sold 4,902 tickets for 100 seats. The four stages are the video's answers in order: let the database decide in one statement, take a lock when one statement can't express the rule, use a version column so nobody holds a lock while thinking, and hold a seat in Redis while the buyer pays. A last section moves payments to a second database, and none of the four stages can fix what that breaks.

| Stage | Technique | What it costs you | Your code |
|---|---|---|---|
| 1 | Conditional update (`... WHERE sold < capacity`) | only rules that fit in one statement | `Tickets/ConditionalTicketSeller.cs` |
| 2 | `SELECT ... FOR UPDATE` | every buyer queues on one row, for as long as the lock is held | `Tickets/LockingTicketSeller.cs` |
| 3 | Optimistic version column, bounded retries | wasted work under contention; Busy answers | `db/migrations/002_*.sql`, `Seats/OptimisticSeatSeller.cs` |
| 4 | Seat hold in Redis with a TTL | a second system that can disagree with the database | `Seats/SeatHolds.cs` |

Everything else (endpoints, baseline sellers, audit, the two-database seller, compose, seed, tests, k6) is written. The stubs throw `NotImplementedException`, so stages 1-4 start red. Three tests are green from the start and must stay green: two baseline guards, and the two-database demonstration.

## The API

Every purchase answers `{outcome, id, expiresAt}`: `Bought` 201, `Held` 200, `SoldOut` / `LimitReached` / `Taken` / `HeldByOther` / `NotHeld` 409, `Busy` 503, `NotFound` 404.

| | |
|---|---|
| `POST /sales/{id}/tickets` | one general-admission ticket (needs `X-Buyer-Id`) |
| `POST /sales/{id}/seats/best` | the lowest-numbered free seat |
| `POST /seats/{id}/hold`, `DELETE /seats/{id}/hold` | hold a seat for `HoldSeconds`, or let it go (stage 4) |
| `POST /seats/{id}/purchase` | buy a specific seat (through its hold under stage 4) |
| `GET /sales/{id}` | the audit: capacity, `sold` column, ticket rows, buyers over the limit, seats sold, seats sold twice |
| `GET /seats/{id}` | one seat |
| `GET /payments/audit` | payments against tickets, across the two databases |
| `GET /stats` | answers by outcome, optimistic conflicts |

There is no auth: `X-Buyer-Id: 42` says who you are.

## Setup

```
cd ~/code/system-design-patterns/03-contention
docker compose up -d --wait                       # sales :55632, payments :55633, redis :56579
dotnet run -c Release --project src/Contention    # applies migrations, listens on :5300; leave it running
docker compose exec postgres psql -U postgres -d sales -f /db/seed.sql -c "CALL seed(100, 2)"
```

The seed creates sale 1: 100 tickets, a limit of 2 per buyer, and 100 seats (A1 to J10). A flash sale sells out, so **reseed before every k6 run**.

Change behavior with environment variables, no code edits: `Sale__Tickets=Conditional|Locking|TwoDatabases`, `Sale__Seats=Optimistic|Held`, `Sale__CheckMilliseconds=50`, `Sale__MaxAttempts=5`, `Sale__HoldSeconds=10`, `Sale__CrashAfterPaymentRate=0.1`. All settings are in `src/Contention/appsettings.json` and `Options.cs`.

Every purchase runs `PurchaseCheck` between deciding and writing: 5 ms by default, standing in for a fraud check or a price lookup. That gap is where buyers overtake each other. `Sale__CheckMilliseconds` widens it.

Tests run on their own containers (Testcontainers), not the compose stack:

```
dotnet test                                # everything, about 15 s
dotnet test --filter-namespace "*Stage2"   # one stage
```

Load test (`k6/flash.js` lists every setting):

```
k6 run k6/flash.js                                  # 5,000 buyers, 500 at a time, one ticket each
k6 run -e CLICKS=3 -e BUYERS=1000 k6/flash.js       # impatient buyers clicking three times at once
k6 run -e MODE=best k6/flash.js                     # best available seat (stage 3)
k6 run -e MODE=hold k6/flash.js                     # hold a random seat, then buy it (stage 4)
```

It counts answers by outcome and prints the sale's audit at the end. The audit is the real result: k6's checks only see status codes.

## How to run each stage

1. Answer the "before you code" questions in writing. Predict the numbers.
2. Reseed, run the k6 command for the stage on the current code, and write the result in the table at the bottom.
3. Write the code until the stage's tests pass.
4. Reseed, run the same k6 command again. Compare with your prediction.
5. Try the mutation listed: break your code on purpose and check a test goes red.

---

## Stage 1: let the database decide

**Before you code**

1. Read `Tickets/NaiveTicketSeller.cs`. Two buyers arrive when `sold` is 99. Write out the interleaving that sells 101 tickets. Then find the second bug: why can `sales.sold` end below 100 while `tickets` has thousands of rows?
2. `UPDATE sales SET sold = sold + 1 WHERE id = 1 AND sold < capacity`. Two of these run at the same moment when `sold` is 99. What does the second one do while the first is uncommitted, and what does it see once the first commits? (Read committed: the second re-checks the `WHERE` on the new row version.)
3. The ticket insert and the counter update must both happen or neither. What happens to a buyer whose update succeeded and whose insert failed, if they are separate transactions?

**Do**

- `Tickets/ConditionalTicketSeller.cs`. The class comment lists what the tests check.
- `dotnet test --filter-namespace "*Stage1"`.
- Reseed, then k6 with `Sale__Tickets=Conditional`. Compare `tickets` and `sold` in the audit with the baseline. Then reseed and run `-e CLICKS=3 -e BUYERS=1000`: what does `buyersOverLimit` say?

**Mutation:** move the capacity check out of the `UPDATE` into the `SELECT` before it. Which tests catch it?

**Afterwards:** the per-buyer limit still breaks under `CLICKS=3`. Can you write it into one statement (`INSERT ... SELECT ... WHERE (SELECT count(*) ...) < limit`)? Why does read committed still let a buyer through twice?

---

## Stage 2: take a lock

**Before you code**

1. Which row do all purchases for one sale have in common? What happens to purchase 2 while purchase 1 holds `FOR UPDATE` on it?
2. The lock is held from the `SELECT ... FOR UPDATE` to the commit. With a 5 ms check inside the lock, what is the most purchases per second for one sale? With `Sale__CheckMilliseconds=50`?
3. A sold-out request still queues for the lock in the simplest version. How could it answer without queuing?

**Do**

- `Tickets/LockingTicketSeller.cs`.
- `dotnet test --filter-namespace "*Stage2"`.
- Reseed, then k6 with `Sale__Tickets=Locking`, with and without `-e CLICKS=3 -e BUYERS=1000`. Then reseed and rerun with `Sale__CheckMilliseconds=50`: what happened to p50 and p99, and why?

**Mutation:** drop `FOR UPDATE`. Which tests catch it?

**Afterwards:** you hold a row lock while the check runs. What if the check calls a payment provider that takes 3 seconds? What would deadlock if a purchase also locked the buyer's row, and another code path locked buyer then sale?

---

## Stage 3: optimistic concurrency

Now reserved seats. `POST /sales/1/seats/best` sells the lowest-numbered free seat, and a version column replaces the lock.

**Before you code**

1. Read `Seats/NaiveSeatSeller.cs`. 5,000 buyers ask for the best seat. Which seat do most of them read, and what does the baseline do to it?
2. With a version check, the 499 buyers who lost the race for seat 1 retry. Which seat do they all read next? How many attempts does it take to sell 100 seats to 500 concurrent buyers this way?
3. When is optimistic better than a lock, and when is it worse? Think about how often two buyers want the same row.

**Do**

- Add `db/migrations/002_<name>.sql` with `ALTER TABLE seats ADD COLUMN version int NOT NULL DEFAULT 0`.
- `Seats/OptimisticSeatSeller.cs`.
- `dotnet test --filter-namespace "*Stage3"`.
- Reseed, then k6 with `Sale__Seats=Optimistic` and `-e MODE=best`. How many `Busy` answers, and how many `conflicts` in the counters? Then try `Sale__MaxAttempts=10`.

**Mutation:** drop `AND version = @version` from your update. Which tests catch it? Then retry without a limit.

**Afterwards:** every buyer here fights over the same lowest seat. Change the selection to a random free seat (or `FOR UPDATE SKIP LOCKED`, if you allow a short lock): what do you predict for conflicts? Where does optimistic concurrency fit better, an edit form or a flash sale?

---

## Stage 4: hold the seat

Real ticketing splits the purchase in two: hold a seat for a few minutes, then pay. The hold has to be fast, exclusive and self-expiring, which Redis is good at.

**Before you code**

1. `SET key value NX PX 60000`. What does `NX` do when 200 buyers send it at once? What does the TTL protect you from?
2. A buyer releases their hold. Why is `GET` then `DEL` wrong, and what does a Lua script change?
3. Buyer A holds seat 5. The hold expires while A's purchase is between its hold check and its database write. B holds seat 5 and buys it. What stops A from overwriting B's purchase? (Look at `SeatRepository.TrySellAsync`.)

**Do**

- `Seats/SeatHolds.cs`.
- `dotnet test --filter-namespace "*Stage4"`. One test waits 1.5 s in real time for Redis to expire a key.
- Reseed, then k6 with `Sale__Seats=Held` and `-e MODE=hold`. Compare `HeldByOther` with `Bought`, and check `seatsSoldTwice`.

**Mutation:** release with a plain `DEL`. Which test catches it? Then hold with `When.Always` instead of `When.NotExists`.

**Afterwards:** question 3 is why the database still does a conditional update after the hold check. Look up fencing tokens: what would a token added to the hold let the database reject? And Redis restarts with no persistence: what happens to every active hold, and is that acceptable here?

---

## Two databases: where these fixes stop

Payments often live in another service with its own database. `Sale__Tickets=TwoDatabases` sells a ticket with the stage 2 lock, writes the payment to the payments database, then commits the ticket. `Sale__CrashAfterPaymentRate` kills a share of purchases between the two commits.

```
Sale__Tickets=TwoDatabases Sale__CrashAfterPaymentRate=0.1 dotnet run -c Release --project src/Contention
docker compose exec postgres psql -U postgres -d sales -f /db/seed.sql -c "CALL seed(100, 2)"
docker compose exec payments psql -U postgres -d payments -c "TRUNCATE payments"
k6 run k6/flash.js
```

The test in `TwoDatabases/` shows the result with every purchase crashing.

1. What does `paymentsWithoutTicket` say? Which lock from stages 1-3 could have prevented it?
2. Swap the order: commit the ticket, then write the payment. What kind of mismatch do you get instead, and which is worse for the business?
3. Two-phase commit across both databases would fix it. What does it cost, and what happens to the tickets row lock while the coordinator is unreachable?

Lab 7 (multi-step processes) builds the answer: idempotency keys and a saga with compensations.

---

## Results

Record your own numbers. Same machine, reseeded before every run.

| Run | k6 command | Bought | tickets / sold | buyersOverLimit | seatsSoldTwice | Busy / conflicts | buy p99 |
|---|---|---|---|---|---|---|---|
| baseline | (default) | | | | | | |
| baseline | `-e CLICKS=3 -e BUYERS=1000` | | | | | | |
| baseline | `-e MODE=best` | | | | | | |
| stage 1 | (default) | | | | | | |
| stage 1 | `-e CLICKS=3 -e BUYERS=1000` | | | | | | |
| stage 2 | `-e CLICKS=3 -e BUYERS=1000` | | | | | | |
| stage 2, `CheckMilliseconds=50` | (default) | | | | | | |
| stage 3 | `-e MODE=best` | | | | | | |
| stage 4 | `-e MODE=hold` | | | | | | |
| two databases, crash 0.1 | (default) | | | | | | |

<details><summary>What the baseline did when I built the lab (WSL2, 2026-11-24)</summary>

5,000 buyers, 500 at a time: 4,902 got `Bought` for 100 tickets, and `sales.sold` ended at 93, because concurrent buyers wrote back the same stale count. Each purchase took 1.8 s at p50, mostly waiting for a pool connection. With 1,000 buyers clicking three times, 2,570 tickets sold and 570 buyers held more than the limit of 2. Best seat: 1,441 purchases reported `Bought` for 100 seats, and every one of the 100 seats was sold more than once.
</details>

## When you finish

1. Write 5 flashcards for the parts you got wrong or had to look up.
2. Ask Claude for a 10-minute mock: "Design Ticketmaster's seat booking", Claude as interviewer. Cover the trade-off of each stage without notes.
3. Not covered here: queueing buyers before they reach the sale (a virtual waiting room), which turns contention into admission control.
