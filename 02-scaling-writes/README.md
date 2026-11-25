# Lab 2: scaling writes

A view counter on Postgres: `POST /videos/{id}/views` runs `UPDATE videos SET views = views + 1` on the video's row. It is correct, and it handles 1,000 views a second when they are spread over many videos. When most of them hit one viral video, every view waits for the row lock the one before it holds, and the app tops out at a few hundred writes a second. The four stages apply the video's fixes in order: write less often, split the hot row, split the data across databases, and put a queue in front with a limit.

| Stage | Technique | What it costs you | Your code |
|---|---|---|---|
| 1 | Batch views in memory, write sums | a crash loses up to one interval of views; reads lag | `Counting/BatchingViewCounter.cs` |
| 2 | Shard one video's count over N rows | reads sum N rows; no cheap "top videos" | `db/migrations/002_*.sql`, `Counting/ShardedViewCounter.cs` |
| 3 | Partition counts over 3 databases with a consistent hash ring | cross-partition queries become scatter-gather | `Partitioning/ConsistentHashRing.cs`, `ScatterGatherTopVideos` in `Partitioning/TopVideos.cs` |
| 4 | Bounded queue, batch drainer, load shedding | callers get 202 (not "written") or 503 | `Queueing/ViewQueue.cs` |

Everything else (endpoints, repositories, flush loop, drainer, compose, seed, tests, k6) is written. The stubs throw `NotImplementedException`, so stages 1-4 start red. Three baseline tests are green and must stay green.

## The API

| | |
|---|---|
| `POST /videos/{id}/views` | one view. 204 written, 202 accepted for later, 404 no such video, 503 shed (with `Retry-After`) |
| `GET /videos/{id}` | `{id, title, views}` |
| `GET /videos/top?limit=10` | most viewed first (501 under the Sharded counter) |
| `GET /stats` | counters since startup: write and read statements per database, views accepted and shed, queue depth |

## Setup

```
cd ~/code/system-design-patterns/02-scaling-writes
docker compose up -d --wait                        # main :55532, partitions :55533-55535
dotnet run -c Release --project src/ScalingWrites  # applies migrations to all four, listens on :5200; Ctrl+C once it starts
docker compose exec postgres psql -U postgres -d videos -f /db/seed.sql -c "CALL seed(100000)"
dotnet run -c Release --project src/ScalingWrites  # leave this running for k6
```

The seed: 100,000 videos with 0-9,999 views each. The partitions start empty.

Change behavior with environment variables, no code edits: `Writes__Counter=Batched|Sharded|Partitioned|Queued`, `Writes__FlushMilliseconds=500`, `Writes__Shards=16`, `Writes__Partitioner=Modulo`, `Writes__QueueCapacity=10000`, `Writes__BatchSize=1`. All settings are in `src/ScalingWrites/appsettings.json` and `Options.cs`.

Tests run on their own Postgres container (Testcontainers), not the compose stack:

```
dotnet test                                # everything, about 20 s
dotnet test --filter-namespace "*Stage2"   # one stage
```

Load test (`k6/views.js` lists every setting):

```
k6 run k6/views.js                         # 1000 req/s, 80% of views on video 1, 5% reads
k6 run -e HOT_SHARE=0 k6/views.js          # the same rate spread over all videos
k6 run -e RATE=3000 -e DURATION=60s k6/views.js
```

It prints p50/p95/p99 for views and reads, how many views were written, accepted and shed, and what the app sent during the run (`app during this run: {...}`).

## How to run each stage

1. Answer the "before you code" questions in writing. Predict the numbers.
2. Run the k6 command for the stage on the current code and write the result in the table at the bottom.
3. Write the code until the stage's tests pass.
4. Run the same k6 command again. Compare with your prediction.
5. Try the mutation listed: break your code on purpose and check a test goes red.

---

## Stage 1: write less often

**Before you code**

1. Run `k6 run -e HOT_SHARE=0 k6/views.js`, then `k6 run k6/views.js`. Same request rate. Why does one collapse? Watch `SELECT pid, wait_event_type, wait_event, query FROM pg_stat_activity WHERE datname = 'videos'` during the second run.
2. One row takes one `UPDATE ... SET views = views + 1` at a time. If a commit takes 2.5 ms, what is the most views per second that row can take, however many connections you add?
3. You flush every second. The process is killed (not stopped). What is lost? What about a clean stop?
4. Your buffer is being flushed while requests keep adding to it. Which two operations must happen together so that no view is lost or written twice?

**Do**

- `Counting/BatchingViewCounter.cs`. The class comment lists what the tests check. `videos.AddViewsAsync` writes a whole dictionary of counts in one statement.
- `dotnet test --filter-namespace "*Stage1"`.
- k6 with `Writes__Counter=Batched`. Compare `counterWrites` with the baseline: how many statements did 30,000 views cost?

**Mutation:** keep the buffer while you write it, and clear it after the write returns. Which test catches it? Run it 3 times. Then: when the write throws, drop the batch. Which test catches that?

**Afterwards:** a user views a video and reloads the page. The count has not moved. Is that a bug? Where else does "write later" show up in this app's API (look at the status code)?

---

## Stage 2: split the hot row

**Before you code**

1. With 8 shard rows per video, how many views can be writing to video 1 at the same moment? What does a read cost now?
2. How should a view pick its shard? What goes wrong with `videoId % Shards`?
3. Batching (stage 1) and sharding both relieve the hot row. Which one loses data on a crash? Which one makes reads slower?

**Do**

- Write `db/migrations/002_<name>.sql` creating `video_view_shards (video_id, shard, views)` with primary key `(video_id, shard)`. The tests and `db/seed.sql` use these names.
- `Counting/ShardedViewCounter.cs`: write to one shard, read the video's `views` column plus the sum of its shards.
- `dotnet test --filter-namespace "*Stage2"`.
- k6 with `Writes__Counter=Sharded`, then `Writes__Shards=2` and `Writes__Shards=32`. Where does adding shards stop helping?

**Mutation:** pick the shard with `videoId % Shards`. Which tests catch it?

**Afterwards:** `GET /videos/top` returns 501 under this counter. What would it cost to rank all videos by the sum of their shards? How would you keep a ranking anyway (a periodic rollup into `videos.views`)?

---

## Stage 3: partition across databases

Stages 1 and 2 still send every write to one Postgres. Partitioning gives each of three databases a third of the videos. Which third is the question.

**Before you code**

1. With `hash(id) % 3`, you add a fourth database. What fraction of videos now map somewhere else? Each of those must be moved before it can be read correctly.
2. With a consistent hash ring, what fraction moves, and where to? Why do you need virtual nodes for an even split?
3. `GET /videos/top?limit=10` used one `ORDER BY`. Each partition now holds part of the counts. What must you ask each one for so that the merged result is right? If one partition takes 500 ms, how long does the top list take?

**Do**

- `Partitioning/ConsistentHashRing.cs`. Use `StableHash.Of`, never `GetHashCode`.
- `ScatterGatherTopVideos` in `Partitioning/TopVideos.cs`.
- `dotnet test --filter-namespace "*Stage3"`. The ring tests need no Docker.
- k6 with `Writes__Counter=Partitioned`. Compare `partitionWrites` across the three. Then run with `Writes__Partitioner=Modulo` and compare.

**Mutation:** give each node one point on the ring instead of `virtualNodes`. Which tests catch it? Then: ask the partitions one after another. Which test catches that?

**Afterwards:** this counter starts every video at 0, because the old counts stay in `videos.views` on the main database. How would you move a live counter from one database to another without losing views or stopping writes? And video 1 is still one row on one partition: what does partitioning do for a single hot key that sharding did not?

---

## Stage 4: a queue, and a limit

**Before you code**

1. The endpoint answers 202 before the view is written. What has the caller been promised? What can still lose the view?
2. The queue holds 50,000 views. Views arrive at 3,000 a second and the drainer writes 1,000 a second. When is the queue full? What happens after that if the queue has no limit?
3. Full queue: wait for space, drop the oldest, or refuse the new view. What does each do to the caller and to the count?

**Do**

- `Queueing/ViewQueue.cs`. `System.Threading.Channels` has bounded channels.
- `dotnet test --filter-namespace "*Stage4"`. Most tests turn the drainer off and drain by hand.
- k6 with `Writes__Counter=Queued`: everything is 202, and `counterWrites` is small.
- Now make the drainer slow and the load sustained, so the drainer falls behind: `Writes__Counter=Queued Writes__BatchSize=1 Writes__QueueCapacity=20000` and `k6 run -e RATE=3000 -e DURATION=60s k6/views.js`. Watch `queueDepth` (`curl -s localhost:5200/stats`) every few seconds. When does shedding start, and what is the latency of an accepted view's write by then?

**Mutation:** create the channel with `BoundedChannelFullMode.DropOldest`. Which test catches it, and what would a user have seen? Then: drain without the `BatchSize` limit.

**Afterwards:** a queue only buys you time. If arrivals stay above the drain rate, the queue fills whatever its size, and a bigger queue only means older views when it does. What are your options once it is full? This queue also lives in the app's memory: what would Kafka or SQS change, and what would they not?

---

## Results

Record your own numbers. Same machine, same seed, same k6 command.

| Run | k6 command | view p50 | view p99 | dropped | written / accepted / shed | counterWrites | partitionWrites |
|---|---|---|---|---|---|---|---|
| baseline | `-e HOT_SHARE=0` | | | | | | |
| baseline | (default) | | | | | | |
| stage 1 | (default) | | | | | | |
| stage 2 | (default) | | | | | | |
| stage 3 | (default) | | | | | | |
| stage 4 | (default) | | | | | | |
| stage 4, `BatchSize=1` | `-e RATE=3000 -e DURATION=60s` | | | | | | |

<details><summary>What the baseline did when I built the lab (WSL2, 2026-11-24)</summary>

At 1,000 req/s spread over 100,000 videos, a view took 3.2 ms at p50 and 7 ms at p99. At the same rate with 80% of views on video 1, the app wrote 410 views a second: p50 5.2 s, p99 7.4 s, 8,356 requests dropped by k6, and reads slowed to 5 s too, stuck behind views for pool connections. At 200 req/s the hot video was fine (2.7 ms p50), so the ceiling for one row is a few hundred commits a second.
</details>

## When you finish

1. Write 5 flashcards for the parts you got wrong or had to look up.
2. Ask Claude for a 10-minute mock: "Design YouTube's view counter", Claude as interviewer. Cover the trade-off of each stage without notes.
3. Not covered here: Kafka-style durable logs, and stream processors that pre-aggregate counts before they reach the database. Stage 4's afterwards question is where they come in.
