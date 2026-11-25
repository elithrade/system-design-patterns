# Lab 7: multi-step processes

A shop checkout makes three calls to three services: charge the card, reserve the stock, create the shipment. The baseline makes them in a row and stops at the first error. When the services fail now and then, it leaves money taken for orders that never ship, stock held forever, and, because clients retry, cards charged twice. In one run of 300 purchases with 10% of calls failing, 76 purchases were charged twice and 134 charges belonged to orders that never shipped. The four stages make every checkout end whole: retries that are safe to repeat, a saga that undoes what it started, recovery after a crash, and then the same workflow on Temporal.

| Stage | Technique | What it costs you | Your code |
|---|---|---|---|
| 1 | Idempotency keys on the API | a table of keys and stored responses; clients must send keys | `Idempotency/IdempotencyStore.cs`, `db/migrations/002_*.sql` |
| 2 | Saga with compensations | every step needs an undo; "cancelled" is a normal outcome | `ResumeAsync` in `Saga/SagaCheckout.cs` |
| 3 | Durable saga: recover after a crash | leases, a recovery loop, and an order state machine you must get right | `Saga/SagaRecovery.cs` |
| 4 | The same workflow on Temporal | another system to run; deterministic workflow code | `Temporal/CheckoutWorkflow.cs` |

Everything else (the three services and their failures, orders, the baseline, Temporal wiring and activities, the audit, tests, k6) is written. The stubs throw `NotImplementedException`, so stages 1-4 start red. Three baseline tests are green and must stay green: they pass by showing what goes wrong.

## How the services fail

Every call can fail in two ways, at random (`Faults__TransientRate`, `Faults__LostResponseRate`) or when a test plans it:

- `ServiceUnavailableException`: nothing happened. Retrying may help.
- `LostResponseException`: it may have happened, and you did not hear back. Retrying may help, if it cannot do the work twice.

Some failures are final (`PermanentFailureException`): card `4000-0000-0000-0002` is declined, `scarce` sells out after 20, and shipping refuses `1 Nowhere Lane`. Tests also throw `ProcessCrashException` to stand for the process dying at a precise point.

Each service takes an idempotency key, like Stripe: the same key charges, reserves or ships once. `Order.KeyFor(op)` derives the keys from the order's `op_key`. Refund and release take the key of the step they undo, so you can undo a step whose response you never got.

## The API

| | |
|---|---|
| `POST /checkout` `{"clientRef", "customerId", "sku", "quantity", "card", "address"}` | 201 completed, 409 cancelled or refused, 503 a service failed, 500 crashed. Optional `Idempotency-Key` header (stage 1) |
| `GET /orders/{id}` | the order: status, reason, the step ids |
| `GET /audit` | the end state across the services: purchases charged twice, charges or held stock for orders that never shipped, stock that does not add up |
| `POST /admin/recover` | run recovery once (stage 3) |

## Setup

```
cd ~/code/system-design-patterns/07-multi-step
docker compose up -d --wait                       # postgres :56032, Temporal :57233, Temporal UI :58233
Faults__TransientRate=0.1 Faults__LostResponseRate=0.05 dotnet run -c Release --project src/MultiStep   # listens on :5700
docker compose exec postgres psql -U postgres -d shop -f /db/seed.sql -c "CALL seed(1000)"
```

Pick the engine with `Checkout__Engine=Naive|Saga|Temporal`. Other settings: `Checkout__MaxAttempts=5`, `Checkout__RetryMilliseconds=50`, `Checkout__LeaseSeconds=30`, `Checkout__RecoverySeconds=5`, `Faults__LatencyMs=20`. All are in `src/MultiStep/appsettings.json` and `Options.cs`. With the Temporal engine the app connects to Temporal at startup and fails if it is not running.

The seed empties every table. Once you have a stage 1 migration, also empty its table between runs.

Tests run on their own containers (Testcontainers): Postgres for every stage, plus a Temporal dev server for stage 4. Each test gets its own database and a fake clock; stage 4 tests get their own task queue.

```
dotnet test                                # everything
dotnet test --filter-namespace "*Stage2"   # one stage
```

Load test (`k6/checkout.js` lists every setting): 300 purchases, 20 at a time, each retried up to 3 times after a 5xx; 5% declined cards, 5% bad addresses, 10% buying `scarce`.

```
k6 run k6/checkout.js             # retries without keys
k6 run -e KEYS=1 k6/checkout.js   # one Idempotency-Key per purchase
```

It ends with the audit. Every number after `orders=` should be 0.

## How to run each stage

1. Answer the "before you code" questions in writing. Predict the numbers.
2. Run the k6 command for the stage on the current code and write the result in the table at the bottom.
3. Write the code until the stage's tests pass.
4. Run the same k6 command again. Compare with your prediction.
5. Try the mutation listed: break your code on purpose and check a test goes red.

---

## Stage 1: idempotency keys

**Before you code**

1. Run `k6 run k6/checkout.js` against the baseline. Why are there more orders than purchases, and where do the double charges come from?
2. The client sends a key and the server stores the response. Two requests with the same key arrive 5 ms apart. What stops both from running?
3. The first request crashed halfway: the card is charged, nothing else happened. The retry arrives with the same key. What must happen for the card to be charged once in total? Who makes that true, your store or the services?

**Do**

- `Idempotency/IdempotencyStore.cs` and `db/migrations/002_<name>.sql`. The class comment lists the rules.
- `dotnet test --filter-namespace "*Stage1"`.
- Restart the app, reseed, and run `k6 run -e KEYS=1 k6/checkout.js`. Which audit numbers went to 0, and which did not?

**Mutation:** never release a key when the handler throws. Which test catches it? Then skip the fingerprint check, and then release the key on `ProcessCrashException` too.

**Afterwards:** how long should you keep keys, and what does a client that retries after that see? Stripe keeps them 24 hours.

---

## Stage 2: a saga

**Before you code**

1. List each step's undo. Shipping has none here: what does that force about the order of the steps?
2. Which failures should you retry, and which should start the undo? What do you do when retries run out?
3. A charge's last attempt lost its response. Did the charge happen? How do you undo something you are not sure happened?
4. The refund fails. And again. When may the saga stop trying?

**Do**

- `ResumeAsync` in `Saga/SagaCheckout.cs`. Save each step's id on the order as soon as you have it: stage 3 resumes from what you saved.
- `dotnet test --filter-namespace "*Stage2"`.
- Restart with `Checkout__Engine=Saga`, reseed, and run `k6 run -e KEYS=1 k6/checkout.js`.

**Mutation:** retry permanent failures too. Which test catches it? Then let an undo give up after `MaxAttempts`, and then undo only steps whose id you saved.

**Afterwards:** between the charge and the refund, the customer's card shows a charge for an order that will be cancelled. Is that acceptable? What would change if you reserved stock first and charged last?

---

## Stage 3: survive a crash

**Before you code**

1. Kill the app with `kill -9` in the middle of a k6 run (`-e RETRIES=0`), start it again, and look at `/audit` and the orders table. What is left halfway, and what would the baseline leave?
2. Your process died after the charge succeeded and before you saved its id. What does resuming do, and why is it safe?
3. The saga had decided to cancel and had released the stock when it died. What must recovery do, and what tells it so?
4. Two instances run recovery at once. How do you keep them off the same order? Lab 5 had the same problem.

**Do**

- `Saga/SagaRecovery.cs`, and whatever stage 3's tests show is missing from your `ResumeAsync`.
- `dotnet test --filter-namespace "*Stage3"`.
- Repeat question 1 with the saga: kill the app mid-run, start it again, wait `LeaseSeconds` plus `RecoverySeconds`, and check `/audit`.

**Mutation:** save `compensating` after undoing instead of before. Which test catches it? Then claim orders without setting a new lease.

**Afterwards:** a checkout that takes longer than `LeaseSeconds` gets resumed by recovery while it is still running. What breaks, and how would you keep the lease fresh?

---

## Stage 4: the same workflow on Temporal

**Before you code**

1. Open the Temporal UI (http://localhost:58233). Which parts of your stages 2 and 3 will Temporal do for you: retries, saved progress, recovery, leases?
2. Workflow code must be deterministic: Temporal replays it from its history after a crash. Why can't it call `DateTime.Now`, `Random` or the database directly?
3. What is still your job: idempotent services, undo logic, deciding what is permanent?

**Do**

- `Temporal/CheckoutWorkflow.cs`. The activities in `Temporal/CheckoutActivities.cs` are written.
- `dotnet test --filter-namespace "*Stage4"`. One test stops a worker in the middle of a charge and starts another.
- Restart with `Checkout__Engine=Temporal`, reseed, run `k6 run -e KEYS=1 k6/checkout.js`, and follow a few workflows in the UI. Then kill the app mid-run and start it again: no recovery code this time.

**Mutation:** leave out `NonRetryableErrorTypes`. Which test catches it? Then run the undo activities with the steps' retry policy.

**Afterwards:** compare your stage 2-3 code with your workflow: lines of code, what each can get wrong, what each costs to run. Which would you choose for a 3-step checkout, and which for a 30-step loan application that waits days for documents?

---

## Results

Record your own numbers. Same machine, the app started with `Faults__TransientRate=0.1 Faults__LostResponseRate=0.05`, reseeded before each run.

| Run | Engine | k6 command | orders | charged twice | paid, not shipped | held, not shipped | checkout p50 / p95 |
|---|---|---|---|---|---|---|---|
| baseline | Naive | `k6 run k6/checkout.js` | | | | | |
| stage 1 | Naive | `-e KEYS=1` | | | | | |
| stage 2 | Saga | `-e KEYS=1` | | | | | |
| stage 3 | Saga | `-e KEYS=1 -e RETRIES=0`, kill -9 mid-run | | | | | |
| stage 4 | Temporal | `-e KEYS=1` | | | | | |
| stage 4 | Temporal | `-e KEYS=1 -e RETRIES=0`, kill -9 mid-run | | | | | |

<details><summary>What I measured when I built the lab (WSL2, 2026-11-24)</summary>

300 purchases, `Faults__LatencyMs=5` unless noted.

| Run | orders | charged twice | paid, not shipped | held, not shipped | checkout p50 / p95 |
|---|---|---|---|---|---|
| Naive, no keys | 457 (252 completed, 205 failed) | 76 | 134 | 56 | 51 / 67 ms |
| Naive, keys | 300 (244 completed, 56 failed) | 0 | 41 | 14 | 57 / 100 ms |
| Saga, keys | 300 (241 completed, 59 cancelled) | 0 | 0 | 0 | 73 / 252 ms |
| Temporal, keys | 300 (234 completed, 66 cancelled) | 0 | 0 | 0 | 262 ms / 2.29 s |

Kill -9 mid-run, `Faults__LatencyMs=20`:

- Saga: 16 orders running and 2 compensating at the kill; the audit showed 8 charges and 2 reservations for unshipped orders. 40 s after the restart, recovery had resumed all 18 and the audit was clean.
- Temporal: 18 orders running at the kill. 30 s after the restart all had finished and the audit was clean, with no recovery code.

Temporal's latency is the dev server's: every activity is a round trip through it, and retries wait `RetryMilliseconds * 2^n`.
</details>

## When you finish

1. Write 5 flashcards for the parts you got wrong or had to look up.
2. Ask Claude for a 10-minute mock: "Design the checkout for an e-commerce site" (or "Design a payment system"), Claude as interviewer. Cover the trade-off of each stage without notes.
3. Not covered here: the outbox pattern (writing to your database and publishing an event atomically), choreographed sagas over events instead of one orchestrator, and human steps that wait days. Lab 9 (capstone) runs its publish pipeline as a Temporal workflow.
