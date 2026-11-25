# Lab 5: long-running tasks

Stride, a fitness app, makes each runner a year-in-review PDF. Rendering one takes 40 seconds. The baseline renders it inside the request, and nginx in front of the app gives up after 30 seconds: every report is a 504, and the app throws the half-done work away. The four stages move the work out of the request and onto a queue in Postgres, then make that queue survive many workers, dead workers, and jobs that never succeed.

| Stage | Technique | What it costs you | Your code |
|---|---|---|---|
| 1 | Job queue and job id | the client polls; a job table to keep | `Jobs/JobStore.cs`, `db/migrations/002_*.sql` |
| 2 | Workers claim with `FOR UPDATE SKIP LOCKED` | order is "roughly oldest first", not strict | `ClaimAsync` in `Jobs/SkipLockedQueue.cs` |
| 3 | Leases, heartbeats, retries with backoff | a job can run more than once, so it must be safe to | `Jobs/SkipLockedQueue.cs` |
| 4 | Dead-letter queue | someone has to look at it | `ListDeadAsync`, `RequeueDeadAsync` in `Jobs/SkipLockedQueue.cs` |

Everything else (endpoints, renderer, the runner with its heartbeat, worker loops, nginx, tests, k6) is written. The stubs throw `NotImplementedException`, so stages 1-4 start red. Two baseline tests are green and must stay green.

## The API

| | |
|---|---|
| `GET /users/{id}/year-in-review?year=2026` | the baseline: render inside the request |
| `POST /users/{id}/year-in-review?year=2026` | 202 with the job and `Location: /jobs/{id}` (stage 1) |
| `GET /jobs/{id}` | the job: `queued` / `running` / `done` / `failed` / `dead`, attempts, last error |
| `GET /jobs/{id}/result` | the PDF once the job is done, 409 before |
| `GET /users/{id}/summary`, `POST /users/{id}/summary` | a 200 ms job, inline or queued (the last question) |
| `GET /jobs/dead`, `POST /jobs/{id}/retry` | dead letters (stage 4) |
| `GET /stats` | renders started, claims, completions, completions refused |

## Setup

```
cd ~/code/system-design-patterns/05-long-running
docker compose up -d --wait                         # postgres :55832, nginx :5580 -> app :5500
dotnet run -c Release --project src/LongRunning     # applies migrations, starts 4 workers, listens on :5500
docker compose exec postgres psql -U postgres -d stride -f /db/seed.sql -c "CALL seed(10000, 200)"
```

The seed makes 10,000 runners with 200 activities each and empties the job queue; run it again to start over. Requests to `localhost:5580` go through nginx and its 30 s timeout. `localhost:5500` is the app without it.

Change behavior with environment variables, no code edits: `Jobs__Queue=SkipLocked` (from stage 2 on), `Jobs__Workers=8`, `Jobs__LeaseSeconds=30`, `Jobs__MaxAttempts=5`, `Reports__RenderMilliseconds=40000`. Rendering user 666's report always throws (`Reports__PoisonUserId`). All settings are in `src/LongRunning/appsettings.json` and `Options.cs`.

Tests run on their own Postgres container (Testcontainers), not the compose stack. Each test gets its own database, a fake clock, no worker loops, and 20 ms renders: tests run jobs by hand.

```
dotnet test                                # everything
dotnet test --filter-namespace "*Stage2"   # one stage
```

Load test (`k6/reports.js` lists every setting):

```
k6 run -e MODE=sync -e USERS=10 k6/reports.js                    # the baseline, through nginx
k6 run -e MODE=queued -e USERS=8 -e VUS=8 -e CLICKS=3 k6/reports.js
```

At the end it prints renders started, claims and completions. More renders than reports means a job ran twice.

## How to run each stage

1. Answer the "before you code" questions in writing. Predict the numbers.
2. Run the k6 command for the stage on the current code and write the result in the table at the bottom.
3. Write the code until the stage's tests pass.
4. Run the same k6 command again. Compare with your prediction.
5. Try the mutation listed: break your code on purpose and check a test goes red.

---

## Stage 1: queue it, return a job id

**Before you code**

1. Run `k6 run -e MODE=sync -e USERS=10 k6/reports.js`. What does the user see, and what happens to the render after nginx gives up? Raise nginx's timeout to 60 s: what breaks next (think about deploys, mobile networks, and how many requests one app process holds open)?
2. The client gets a job id. How does it find out the job is done? Compare polling, a webhook to the client, and pushing over a connection (lab 6). What should the poll interval be for a 40 s job?
3. A runner clicks Generate five times because nothing seems to happen. How many PDFs should you render? Where does "only one active job per user and year" have to be enforced so that five clicks arriving at once still make one job?

**Do**

- `Jobs/JobStore.cs` and `db/migrations/002_<name>.sql`. The class comment lists what the tests check.
- `dotnet test --filter-namespace "*Stage1"`.
- Restart the app and run `k6 run -e MODE=queued -e USERS=8 -e VUS=8 -e CLICKS=3 k6/reports.js`. Every report arrives now. Compare renders with reports in the last line.

**Mutation:** look for an active job with a SELECT, and INSERT if there is none. Which test catches it?

**Afterwards:** the queue is a table in the same Postgres as the data. Name what you would gain and lose by moving it to SQS, RabbitMQ or Redis. What does enqueueing in the same transaction as a business write give you that a separate broker cannot?

---

## Stage 2: many workers, no double runs

**Before you code**

1. The last k6 run rendered some reports more than once. Read `Jobs/NaiveQueue.cs`: how do two workers end up with the same job? Why does it happen more when the workers poll in step?
2. `SELECT ... FOR UPDATE` fixes the double run. What does every other worker do while the first holds the lock? What does `SKIP LOCKED` change?
3. With `SKIP LOCKED`, is the oldest job always the next one to run? Does it matter for reports?

**Do**

- `ClaimAsync` in `Jobs/SkipLockedQueue.cs`, one statement or one transaction.
- `dotnet test --filter-namespace "*Stage2"`.
- Restart the app with `Jobs__Queue=SkipLocked`, run `CALL seed(10000, 200)` again, then the stage 1 k6 command. Renders should equal reports. Try `Jobs__Workers=16`.

**Mutation:** drop `SKIP LOCKED` (keep `FOR UPDATE`). Which test catches it? Then drop the lock entirely.

**Afterwards:** each idle worker polls every `PollMilliseconds`. With 50 app instances and 8 workers each, how many queries per second hit Postgres when the queue is empty? Look up `LISTEN/NOTIFY` and say how it would change that.

---

## Stage 3: leases, heartbeats and retries

**Before you code**

1. A worker claims a job and the process is killed (`kill -9`, a deploy, an OOM). With the naive queue, what state is the job in a day later?
2. A lease says "this worker holds the job until T". What should T be for a 40 s render, and what does the heartbeat in `Jobs/JobRunner.cs` let you do instead of guessing?
3. A worker freezes for 45 s (a GC pause, a stalled disk), its lease lapses, another worker takes the job, and then the first one wakes up and finishes. What must `CompleteAsync` check so the second run's state is not overwritten? What if both write a file somewhere?
4. The map tile server is down for 2 minutes. Every job fails. When should each retry happen?

**Do**

- The rest of `Jobs/SkipLockedQueue.cs` except stage 4's two methods.
- `dotnet test --filter-namespace "*Stage3"`.
- With the app running on `SkipLocked`, queue a report, kill the app mid-render, start it again, and watch the job:

  ```
  curl -s -X POST localhost:5500/users/42/year-in-review
  kill -9 $(pgrep -f net10.0/LongRunning)
  dotnet run -c Release --project src/LongRunning      # with Jobs__Queue=SkipLocked
  watch -n 2 'docker compose exec postgres psql -U postgres -d stride -c "SELECT id, status, attempts, lease_until FROM jobs WHERE user_id = 42"'
  ```

  The job stays `running` until its lease runs out (30 s), then a worker claims attempt 2 and finishes it.

**Mutation:** let `CompleteAsync` update by `id` alone. Which tests catch it? Then use a constant backoff, and then stop claiming jobs whose lease expired.

**Afterwards:** "at least once" is what every queue here gives you: a job can run twice. What would make sending an email from a job safe to repeat? Lab 7 (multi-step processes) builds on this with idempotency keys.

---

## Stage 4: dead letters

**Before you code**

1. User 666's report always throws. With retries and no limit, what does that job cost you over a week? What happens to everyone else's jobs if there are a thousand like it?
2. After how many attempts should a job stop, and should that depend on the error? Which errors are worth retrying at all?
3. A person fixes the bug behind a dead job. What should "retry" reset, and what should it keep?

**Do**

- `ListDeadAsync`, `RequeueDeadAsync`, and the dead branch in `FailAsync`.
- `dotnet test --filter-namespace "*Stage4"`.
- `curl -s -X POST localhost:5500/users/666/year-in-review`, wait for the retries (2+4+8+16 s of backoff plus the renders), then `curl -s localhost:5500/jobs/dead`.

**Mutation:** never mark a job dead. Which tests catch it? Then requeue without resetting `attempts`.

**Afterwards:** who gets told when a job dies, and how? Write the alert you would want: what it says, who it pages, and when it should not fire.

---

## The last question: should a 200 ms job be queued?

```
k6 run -e MODE=summary-sync -e USERS=200 -e VUS=20 -e BASE_URL=http://localhost:5500 k6/reports.js
k6 run -e MODE=summary-queued -e USERS=200 -e VUS=20 -e POLL_MS=100 -e BASE_URL=http://localhost:5500 k6/reports.js
```

Compare time to a result and the number of requests. When would you queue it anyway?

---

## Results

Record your own numbers. Same machine, seeded with `CALL seed(10000, 200)` before each run.

| Run | k6 command | reports ok | renders | job_seconds p50 | notes |
|---|---|---|---|---|---|
| baseline | `-e MODE=sync -e USERS=10` | | | | 504s? |
| stage 1 | `-e MODE=queued -e USERS=8 -e VUS=8 -e CLICKS=3` | | | | Naive queue |
| stage 2 | same, `Jobs__Queue=SkipLocked` | | | | |
| stage 3 | kill -9 mid-render | | | | job finished? |
| summary inline | `-e MODE=summary-sync ...` | | | | |
| summary queued | `-e MODE=summary-queued ...` | | | | |

<details><summary>What I measured when I built the lab (WSL2, 2026-11-24, 4 workers)</summary>

- Sync through nginx, 10 reports: all 10 got 504 at 30.0 s.
- Queued on the naive queue, 8 users clicking 3 times each: 24 POSTs made 8 jobs, which took 15 renders. Median time to a report 101 s, last one 161 s. In an earlier run, 4 workers in step claimed the same single job and rendered it 4 times.
- Same run on `SKIP LOCKED`: 8 renders. Median 60 s, last one 80 s (two rounds of 40 s on 4 workers).
- `kill -9` mid-render: the job stayed `running` until its lease lapsed, then attempt 2 finished it.
- The 200 ms summary, 200 users, 20 at a time: inline, median 0.20 s and 202 requests. Queued, median 1.02 s and 2,202 requests.
</details>

## When you finish

1. Write 5 flashcards for the parts you got wrong or had to look up.
2. Ask Claude for a 10-minute mock: "Design a system that generates reports that take minutes", Claude as interviewer. Cover the trade-off of each stage without notes.
3. Not covered here: priorities and fair sharing between tenants, cancelling a running job, and progress reporting (lab 6 pushes progress; lab 9 uses it). Lab 7 (multi-step processes) builds on this lab's retries.
