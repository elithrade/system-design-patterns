# System design patterns, hands on

One lab per pattern from Hello Interview's [8 Patterns to Pass Your System Design Interview](https://www.youtube.com/watch?v=Rd_2oviGqnA), plus a capstone that combines four of them. Each lab is a small .NET 10 service that works for one user and breaks under load or failure. You fix it in stages and measure each one.

## The loop

1. **Baseline.** Run the naive service and the lab's k6 script. Write the numbers down.
2. **Predict.** Answer the stage's "before you code" questions in writing before touching code.
3. **Build.** Fill in the stub until that stage's tests pass. The tests define "done"; there are no solutions in this repo.
4. **Measure.** Run the same k6 command again and compare it with your prediction.
5. **Break it.** Apply the mutation the stage suggests and check a test goes red.
6. **Explain.** At the end of a lab, write flashcards for what you missed and do a 10-minute mock interview with Claude on the matching Hello Interview problem.

## Labs

| # | Pattern | Lab | What breaks | Stages | Status |
|---|---|---|---|---|---|
| 1 | Scaling reads | Instagram-style profiles and posts, 2M posts | Full scans, likes counted per read, replica lag, one hot key | index + denormalize → replica + read-your-writes → Redis + invalidation → in-process cache for hot keys | ready |
| 2 | Scaling writes | View/like counters | One hot row serializes writes; a burst that never ends | batching → sharded counters → hash partitioning over 3 Postgres → queue + load shedding, then sustained overload to show "a queue only buys you time" | |
| 3 | Dealing with contention | Flash sale: 100 seats, 5,000 buyers | Read-then-write oversells | conditional update → `SELECT FOR UPDATE` → optimistic version column → Redis seat hold with TTL; then split seats and payments across 2 databases and watch the single-DB fixes stop working | |
| 4 | Large blobs | File upload service on MinIO | A 2 GB upload through the API | presigned PUT → multipart/resumable → bucket notifications update metadata → orphan cleanup → signed downloads | |
| 5 | Long-running tasks | "Stride" year-in-review PDF | nginx cuts the request at 30 s | job queue + job ID → `SKIP LOCKED` workers → worker crash and retries → dead-letter queue; plus a 200 ms job, sync vs queued | |
| 6 | Realtime updates | Live auction bids | Polling load; a second instance misses bids | polling → long polling → SSE → WebSocket → Redis pub/sub across instances → reconnect with `Last-Event-ID` | |
| 7 | Multi-step processes | Checkout: payment, inventory, shipping, with injected failures | Half-done orders | idempotency keys → hand-rolled saga with compensations → the same workflow in Temporal, compared | |
| 8 | Proximity search | Nearby drivers, 1M points | Haversine scan over every row | geohash prefix column → PostGIS GiST → Redis `GEOSEARCH` → location updates every 3 s; scan vs index at 1k/100k/1M rows | |
| 9 | Capstone | Mini video platform | | presigned upload (4) → transcode job (5) → SSE progress bar (6) → Temporal workflow from upload to publish (7) | |

Labs 2-9 are scaffolded when you reach them, in the same shape as lab 1, adjusted by what lab 1 taught about stage size.

## Each lab

```
NN-name/
  README.md        the problem, setup, stages with questions, results table
  compose.yaml     the lab's infrastructure (its own ports, so labs don't collide)
  *.slnx
  src/             the service: baseline written, your parts as stubs that throw NotImplementedException
  tests/           one namespace per stage: dotnet test --filter-namespace "*Stage2"
  k6/              the load script that shows the problem
  db/              migrations and seed, where the lab has a database
```

## Needs

.NET 10 SDK, Docker (tests use Testcontainers), k6. Tests run on Microsoft.Testing.Platform (`global.json`).
