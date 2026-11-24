# Lab 1: scaling reads

A small Instagram-style API on Postgres: users, posts, likes. It is correct and slow. Read traffic outgrows the database first (10:1 to 100:1 reads to writes is normal), and the four stages here apply the fixes in the order the video gives them: fix the queries, add replicas, add a cache, then deal with the one hot key.

| Stage | Technique | What it costs you | Your code |
|---|---|---|---|
| 1 | Index + denormalized like count | every like now writes two rows | `db/migrations/002_*.sql`, `Data/PostQueries.cs` |
| 2 | Read replica + read-your-writes | replicas lag, so someone sees old data | `Routing/ReadYourWritesRouter.cs` |
| 3 | Redis cache-aside + invalidation | two copies of the truth | `Caching/CachedPostReader.cs` |
| 4 | In-process cache for hot keys | bounded staleness | `Caching/HotKeyPostReader.cs` |

Everything else (endpoints, repository, Redis wrapper, compose, seed, tests, k6) is written. The stubs throw `NotImplementedException`, so stages 2-4 start red. Stage 1 starts with four red plan tests and four green guard tests that must stay green.

## The API

| | |
|---|---|
| `GET /users/{id}/posts?limit=20` | a user's posts, newest first, with author name and like count |
| `GET /posts/{id}` | one post |
| `POST /posts` `{"caption": "..."}` | create a post (needs `X-User-Id`) |
| `POST /posts/{id}/likes` | like a post; liking twice is a no-op (needs `X-User-Id`) |
| `GET /stats` | counters since startup: queries per database, post reads, Redis commands |

There is no auth: `X-User-Id: 42` says who you are. Every read response has an `X-Db: primary|replica` header.

## Setup

```
cd ~/code/system-design-patterns/01-scaling-reads
docker compose up -d --wait                       # primary :55432, replica :55433, redis :56379
dotnet run -c Release --project src/ScalingReads  # applies migrations, listens on :5100; Ctrl+C once it starts
docker compose exec postgres psql -U postgres -d feed -f /db/seed.sql \
  -c "CALL seed(200000, 2000000, 3000000, 10, 150000)"   # about a minute
dotnet run -c Release --project src/ScalingReads  # leave this running for k6
```

The seed: 200,000 users, 2,000,000 posts, about 4.5M likes. Posts 1-10 are celebrity posts with 150,000 likes each. The replica copies all of it through streaming replication, and holds every change back by 2 seconds (`recovery_min_apply_delay`) so lag is visible on demand.

Change behavior with environment variables, no code edits: `Reads__Router=ReadYourWrites`, `Reads__Cache=Redis`, `Reads__L1Seconds=2`. All settings are in `src/ScalingReads/appsettings.json` and `Options.cs`.

Tests run on their own containers (Testcontainers), not the compose stack:

```
dotnet test                                # everything, about 15 s
dotnet test --filter-namespace "*Stage2"   # one stage
```

Load test (`k6/reads.js` lists every setting):

```
k6 run k6/reads.js                               # 200 req/s, half profile pages, half posts, 2% likes
k6 run -e RATE=300 k6/reads.js                   # more load
k6 run -e HOT_SHARE=0.8 -e POST_SHARE=0.9 k6/reads.js   # most reads hit the 10 celebrity posts
```

It prints p50/p95/p99 per endpoint and, at the end, what the app sent during the run (`app during this run: {...}`).

## How to run each stage

1. Answer the "before you code" questions in writing. Predict the numbers.
2. Run the k6 command for the stage on the current code and write the result in the table at the bottom.
3. Write the code until the stage's tests pass.
4. Run the same k6 command again. Compare with your prediction.
5. Try the mutation listed: break your code on purpose and check a test goes red.

---

## Stage 1: make the database do less work

**Before you code**

1. Run `EXPLAIN ANALYZE` on `PostQueries.UserPosts` for user 5 (`docker compose exec postgres psql -U postgres -d feed`, replace `@userId` with 5 and `@limit` with 20). Which node takes the time, and how many rows does it read to return 20?
2. What index lets Postgres find a user's 20 newest posts and stop? Does column order matter?
3. `GET /posts/1` and `GET /posts/1000000` run the same query. Which is slower, and why? How does the gap grow as the post gets more popular?
4. If `posts` stores a `like_count`, what must every like do? What happens when two users like the same post at the same moment? When one user likes it twice?

**Do**

- Add `db/migrations/002_<name>.sql`. Name the column `like_count`: `db/seed.sql` rebuilds it after a reseed (or edit the seed to match your name). The app applies the migration when it starts; on 2M posts that takes a few seconds.
- Change the queries in `Data/PostQueries.cs`, including `Like`, so the count stays right. Keep it to one statement or one transaction.
- `dotnet test --filter-namespace "*Stage1"`. The plan tests run `EXPLAIN` on your queries; the like tests check the count under concurrent and duplicate likes.

**Mutation:** make a repeated like increment the count. Which test catches it?

**Afterwards:** your migration took a lock on `posts` while it built the index. What would block on a live system, and what does `CREATE INDEX CONCURRENTLY` change (it cannot run inside a transaction, so the migrator here can't use it)?

---

## Stage 2: read replicas

First, see the bug. Restart the app with every read on the replica, then post and read your own profile at once:

```
Reads__Router=Replica dotnet run -c Release --project src/ScalingReads

curl -s -X POST localhost:5100/posts -H 'X-User-Id: 5' -H 'Content-Type: application/json' -d '{"caption":"just posted"}'
curl -s -i 'localhost:5100/users/5/posts?limit=1' -H 'X-User-Id: 5'     # X-Db: replica, and no new post
sleep 2.5; curl -s 'localhost:5100/users/5/posts?limit=1' -H 'X-User-Id: 5'   # there it is
```

**Before you code**

1. Who is hurt by the lag: the user who posted, or everyone else? Which of them would notice?
2. Three ways to route a read: by endpoint, by time since the user's last write, by replication position. What does each cost?
3. `StickySeconds` is 5 and the replica lags 2 s. What happens if the lag grows to 30 s (`REPLICA_DELAY=30s docker compose up -d replica`)?

**Do**

- `Routing/ReadYourWritesRouter.cs`: after a user writes, their reads go to the primary for `StickySeconds`; all other reads go to the replica. Use the injected `TimeProvider`.
- `dotnet test --filter-namespace "*Stage2"`. The tests use a "replica" that never receives a write.
- Run k6 with `Reads__Router=ReadYourWrites`. How do `primaryQueries` and `replicaQueries` split? Raise `LIKE_SHARE` to 0.3: what happens to the split, and why?

**Mutation:** use `DateTimeOffset.UtcNow` in place of the `TimeProvider`. Which test catches it?

**Afterwards:** your router keeps its state in memory. Run three app instances behind a load balancer: what breaks? Sketch a fix that works across instances: a cookie with a timestamp, or the primary's WAL position (`pg_current_wal_lsn()`) compared with the replica's (`pg_last_wal_replay_lsn()`).

---

## Stage 3: cache in Redis, and keep it right

You built cache-aside with single-flight in url-shortener. Links never change, so you never had to invalidate. Posts change with every like, so invalidation is the subject here.

**Before you code**

1. The obvious invalidation is "on a like, delete the cached post". A celebrity post gets 1,000 likes a second and 50,000 reads a second. What is its hit rate?
2. Which part of a post changes often, and which almost never? What if they were cached under different keys?
3. A like arrives for a post that is not in the cache. What does `INCR` do to a key that does not exist, and what would the next read return?
4. Find the race: a read misses, loads the count from the database, and a like commits before the read writes the count back to Redis. What does the cache hold, and for how long?

**Do**

- `Caching/CachedPostReader.cs`. The class comment lists what the tests check. `ICache` has `GetMany` (MGET), `Increment` and `Eval` (a Lua script runs atomically inside Redis).
- `dotnet test --filter-namespace "*Stage3"`.
- k6 with `Reads__Cache=Redis` and `-e HOT_SHARE=0.8 -e POST_SHARE=0.9`: compare `postReads` and `redisCommands` with stage 1, and the latency of `post`.

**Mutation:** replace your like handling with a GET, add 1, then SET. Which test catches it? Run the tests 3 times.

**Afterwards:** how would you bound the damage from the race in question 4 without a lock?

---

## Stage 4: the hot key

At 80% hot share, nearly every request asks Redis for the same 10 keys. In a Redis cluster each key lives on one node, so one node takes all of it while the others idle, and adding nodes does not help.

**Before you code**

1. With 3 app instances and `L1Seconds` = 1, at most how many Redis reads per second does one hot post cost? With 300 instances?
2. What does a user see in exchange? Which data in this app could tolerate 1 second of staleness, and which could not?
3. 500 requests for the same post arrive while the local copy is missing. How many Redis reads should that cost, and what makes it one?

**Do**

- `Caching/HotKeyPostReader.cs`, in front of `CachedPostReader`. Use the injected `TimeProvider` for expiry.
- `dotnet test --filter-namespace "*Stage4"`.
- k6 with `-e HOT_SHARE=0.8 -e POST_SHARE=0.9`, once with `Reads__Cache=Redis` and once with `Reads__Cache=RedisL1`. Compare `redisCommands`.

**Mutation:** check the dictionary and load on a miss without sharing the in-flight load. Which tests catch it?

**Afterwards:** caching every post locally wastes memory on posts read once. How would you find the hot keys and cache only those? Another fix splits a hot key into copies (`post:1#0` to `post:1#7`) spread across nodes: compare the two.

---

## Results

Record your own numbers. Same machine, same seed, same k6 command.

| Run | k6 command | profile p99 | post p99 | dropped | primary / replica queries | postReads | redisCommands |
|---|---|---|---|---|---|---|---|
| baseline | `-e RATE=300` | | | | | | |
| stage 1 | `-e RATE=300` | | | | | | |
| baseline | `-e HOT_SHARE=0.8 -e POST_SHARE=0.9` | | | | | | |
| stage 1 | same | | | | | | |
| stage 2 | `-e RATE=300` | | | | | | |
| stage 3 | `-e HOT_SHARE=0.8 -e POST_SHARE=0.9` | | | | | | |
| stage 4 | same | | | | | | |

<details><summary>What the baseline did when I built the lab (WSL2, 2026-11-24)</summary>

At 100 req/s the profile page took 27 ms at p50 (a parallel scan of 2M posts) and a post took about 1 ms. At 300 req/s the app fell over: p50 1.03 s, p99 2 s for every endpoint, 358 requests dropped. Posts and likes slowed down too, because they waited for pool connections held by profile scans. With 80% of reads on celebrity posts, a post read took 7 ms at p50 instead of 0.8 ms, from counting 150,000 likes per read.
</details>

## When you finish

1. Write 5 flashcards for the parts you got wrong or had to look up.
2. Ask Claude for a 10-minute mock: "Design the read path for Instagram's profile page", Claude as interviewer. Cover the trade-off of each stage without notes.
3. Not covered here: CDNs for static media (the video's last read-scaling step). Large blobs (lab 4) serves media through a CDN.
