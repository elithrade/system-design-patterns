# Lab 8: proximity search

A ride app: drivers report where they are every few seconds, and a rider asks for the nearest available drivers within 2 km. The baseline computes the distance from the rider to every available driver and keeps the close ones. With 1,000 drivers that takes 1 ms. With 1,000,000 it takes 59 ms per search, and the work grows with the city, not with the answer. The four stages find nearby drivers without looking at the far ones: a geohash prefix, a PostGIS spatial index, Redis geo sets, and then dropping drivers who stopped reporting.

| Stage | Technique | What it costs you | Your code |
|---|---|---|---|
| 1 | Geohash column and prefix query | 9 cells read to answer one circle; cell edges to handle | `Geo/Geohash.cs`, `Search/GeohashIndex.cs`, `db/migrations/002_*.sql` |
| 2 | PostGIS geography and a GiST index | an extension to run; you must read query plans | `Search/PostGisIndex.cs`, `db/migrations/003_*.sql` |
| 3 | Redis `GEOADD` / `GEOSEARCH` | locations live in memory, apart from the database | `Search/RedisDriverIndex.cs` |
| 4 | Drop drivers who stop reporting | a second sorted set and a prune job | `Search/RedisDriverIndex.cs` again |

Everything else (the drivers table, the baseline scan, endpoints, the copy from Postgres into Redis, tests, k6) is written. The stubs throw `NotImplementedException`, so stages 1-4 start red. Three baseline tests are green and must stay green.

## The API

| | |
|---|---|
| `GET /drivers/nearby?lat=40.72&lng=-73.99&radiusM=2000&limit=10` | available drivers in range, nearest first, with `distanceM` |
| `POST /drivers/{id}/location` `{"lat", "lng", "available"}` | one driver's report |
| `POST /drivers/locations` `[{"driverId", "lat", "lng", "available"}, ...]` | a batch of reports, as a location-ingest service would forward them |
| `POST /admin/load` | copy every driver from Postgres into the index (stage 3: Redis starts empty) |
| `POST /admin/prune` | remove drivers who stopped reporting from Redis (stage 4) |

## Setup

```
cd ~/code/system-design-patterns/08-proximity
docker compose up -d --wait                       # PostGIS :56132, redis :57079
dotnet run -c Release --project src/Proximity     # applies migrations, listens on :5800
docker compose exec postgres psql -U postgres -d rides -f /db/seed.sql -c "CALL seed(100000)"
```

`seed(n)` replaces every driver with n drivers at random points in a 39 x 30 km box around New York, 80% of them available. The same n always gives the same drivers. Try 1,000, 100,000 and 1,000,000.

Pick the engine with `Proximity__Engine=Scan|Geohash|PostGis|Redis`. Other settings: `Proximity__StaleSeconds=30`, `Proximity__KeyPrefix=lab8:`. All are in `src/Proximity/appsettings.json` and `Options.cs`. With the Redis engine, run `curl -s -X POST localhost:5800/admin/load` after each start or seed.

Postgres runs with `jit=off` in compose and in the tests. The planner badly underestimates how many rows `ST_DWithin` matches, prices the query high, and JIT-compiles it on every search. At 1M drivers that added 3.4 ms to a 1.1 ms query, and 105 ms to a 30 ms one.

Tests run on their own containers (Testcontainers): PostGIS with 100,000 drivers for every test, and Redis. Each test gets its own database clone, its own Redis key prefix, and a fake clock. The baseline scan is the reference: every engine must return what the scan returns, within 5 m (Redis rounds positions).

```
dotnet test                                # everything
dotnet test --filter-namespace "*Stage2"   # one stage
```

Load test (`k6/nearby.js` lists every setting): riders search at 20 a second from random points in the box.

```
k6 run k6/nearby.js                     # searches only
k6 run -e DRIVERS=100000 k6/nearby.js   # plus 100,000 drivers reporting every 3 s (33,000 updates a second)
```

## How to run each stage

1. Answer the "before you code" questions in writing. Predict the numbers.
2. Run the k6 command for the stage on the current code and write the result in the table at the bottom.
3. Write the code until the stage's tests pass.
4. Run the same k6 command again. Compare with your prediction.
5. Try the mutation listed: break your code on purpose and check a test goes red.

---

## Stage 1: geohash

**Before you code**

1. Run `k6 run k6/nearby.js` against the baseline after `seed(1000)`, `seed(100000)` and `seed(1000000)`. How does search time grow with drivers? Why can't a btree index on `lat` or on `lng` alone fix it?
2. Encode one point by hand to 3 characters: Times Square is (40.758, -73.9855). Each character halves the cell 5 times. How wide is a 5-character cell in New York, and a 6-character one?
3. A rider stands 10 m from the edge of their cell, and the nearest driver is 20 m away in the next cell. What does a query on the rider's cell alone miss? Why are 9 cells enough, and what does that say about the cell size you pick for a 2 km radius?
4. Why does `geohash LIKE 'dr5ru%'` use a btree index only with `COLLATE "C"` or `text_pattern_ops`? What range condition (`>=` and `<`) asks the same thing?

**Do**

- `Geo/Geohash.cs`, then the `002_<name>.sql` migration, then `NearbyAsync` in `Search/GeohashIndex.cs`. The comments list what the tests check.
- `dotnet test --filter-namespace "*Stage1"`. `Encode` is compared with PostGIS's `ST_GeoHash` on random points.
- Restart with `Proximity__Engine=Geohash` and run the k6 command at each seed size.

**Mutation:** split latitude first instead of longitude. Which tests catch it? Then search only the rider's cell, and then pick cells a quarter of the radius across.

**Afterwards:** at 1M drivers a 2 km search over 9 cells of precision 5 reads about 160 km² of drivers to answer a 12.6 km² circle. Would precision 6 help? What does it cost near cell edges, and how many cells would you need?

---

## Stage 2: PostGIS

**Before you code**

1. A GiST index on points is an R-tree: boxes inside boxes. How does it answer "everything within 2 km" without a fixed cell size?
2. `geography` measures on the sphere or the spheroid; `geometry` in SRID 4326 measures in degrees. What is wrong with `ST_DWithin(geom, point, 0.02)` as "within 2 km" in New York? How different are sphere and spheroid distances over 2 km?
3. The rider wants the nearest 10 drivers within 5 km, and 5,000 drivers are within 5 km. Which of those 5,000 does the database have to look at? Is "within 5 km, nearest first, at most 10" the same set as "the nearest 10, minus any beyond 5 km"?

**Do**

- The `003_<name>.sql` migration and `NearbyAsync` in `Search/PostGisIndex.cs`.
- `dotnet test --filter-namespace "*Stage2"`. Same tests as stage 1, plus a 5 km search with a limit of 10. If that one fails, run `EXPLAIN (ANALYZE, BUFFERS)` on your query for a 5 km search (psql's `PREPARE` and `EXECUTE` give you the parameters the app sends) and count the rows it reads.
- Restart with `Proximity__Engine=PostGis` and run the k6 command at each seed size.

**Mutation:** measure on the spheroid (leave out `use_spheroid false`). Which test catches it? Then drop the GiST index.

**Afterwards:** rows in the table are in id order, so the drivers near one rider sit on different pages. Look at the `Buffers:` line of a 2 km search, then `CLUSTER drivers USING <your index>` and look again. Why doesn't clustering last in a table where every row moves every 3 seconds?

---

## Stage 3: Redis

**Before you code**

1. A Redis geo set is a sorted set whose scores are 52-bit geohashes. How does `GEOSEARCH` find drivers within 2 km with that? What does it have in common with your stage 1?
2. 1M drivers in a geo set: how much memory? Redis keeps it all in RAM. What happens to the set when Redis restarts, and where would you rebuild it from?
3. A batch of 500 reports: what does one command per driver cost in round trips, and what do `IBatch` or the array overloads change?

**Do**

- Stage 3's part of `Search/RedisDriverIndex.cs`.
- `dotnet test --filter-namespace "*Stage3"`. The tests load 20,000 drivers through your `UpsertManyAsync`.
- Restart with `Proximity__Engine=Redis`, `curl -s -X POST localhost:5800/admin/load`, and run the k6 command at each seed size. Then `k6 run -e DRIVERS=100000 k6/nearby.js` with Redis and with PostGIS.

**Mutation:** search with the radius in kilometers. Which tests catch it? Then ask for any order instead of nearest first, and then leave busy drivers in the geo set.

**Afterwards:** PostGIS searched faster than Redis in my runs, and took 33,000 updates a second. When would you still move locations to Redis? Think about write volume at 10 times the drivers, what else uses the Postgres primary, and whether a location from 3 seconds ago needs a transaction.

---

## Stage 4: drivers who stop reporting

**Before you code**

1. A driver's phone dies in the middle of a shift. What do riders see with your stage 3 code, and for how long?
2. Redis can expire a key, but not one member of a sorted set. What do you keep instead, and how do you find the drivers not seen for 30 seconds without reading every driver?
3. Filter stale drivers on every search, prune them in the background, or both? What does each cost, and what does a search see between two prunes?

**Do**

- Stage 4's part of `Search/RedisDriverIndex.cs`: record when each driver last reported, filter stale drivers out of `NearbyAsync`, and write `PruneAsync`. Use the injected `TimeProvider`.
- `dotnet test --filter-namespace "*Stage4"`. The tests move the fake clock.
- Run the app with `Proximity__StaleSeconds=10`, load, wait 15 s, search, then `curl -s -X POST localhost:5800/admin/prune`.

**Mutation:** skip the staleness filter in `NearbyAsync`. Which tests catch it? Then count a driver seen exactly `StaleSeconds` ago as fresh, and then prune only the seen set.

**Afterwards:** search and the stale filter are two Redis commands. Which drivers can a search return that went stale between them, and does it matter here? How would a Lua script or `GEOSEARCHSTORE` change that?

---

## Results

Record your own numbers. Same machine, 20 searches a second, 2 km radius, limit 10.

| Run | engine | seed | search p50 / p95 | report p50 / p99 | notes |
|---|---|---|---|---|---|
| baseline | Scan | 1,000 | | | |
| baseline | Scan | 100,000 | | | |
| baseline | Scan | 1,000,000 | | | |
| stage 1 | Geohash | 1,000,000 | | | |
| stage 2 | PostGis | 1,000,000 | | | |
| stage 3 | Redis | 1,000,000 | | | |
| stage 2 | PostGis | 1,000,000, `-e DRIVERS=100000` | | | |
| stage 3 | Redis | 1,000,000, `-e DRIVERS=100000` | | | |

<details><summary>What I measured when I built the lab (WSL2, 2026-11-24)</summary>

Search p50, 2 km, limit 10:

| drivers | Scan | Geohash | PostGIS | Redis |
|---|---|---|---|---|
| 1,000 | 0.96 ms | 1.02 ms | 0.87 ms | 0.93 ms |
| 100,000 | 10.9 ms | 4.9 ms | 0.92 ms | 1.41 ms |
| 1,000,000 | 59.1 ms | 31.0 ms | 0.97 ms | 5.7 ms |

1,000,000 drivers, 100,000 of them reporting every 3 s in batches of 500 (33,000 updates a second):

| engine | report p50 / p99 | search p50 / p99 |
|---|---|---|
| Scan | 12.0 / 117 ms | 71.4 / 94.7 ms |
| Geohash | 11.0 / 120 ms | 36.8 / 48.4 ms |
| PostGIS | 11.2 / 57.6 ms | 1.09 / 8.15 ms |
| Redis | 2.95 / 7.58 ms | 6.24 / 9.94 ms |

All 1,000,000 reporting (333,000 updates a second) saturated the app process and k6 on the same machine with either engine, about 350 requests a second, so that run says nothing about Postgres against Redis.

My first PostGIS query passed every stage 2 test but the 5 km one: 11 ms against the scan's 10.6 ms. After reading its plan it took 0.34 ms.
</details>

## When you finish

1. Write 5 flashcards for the parts you got wrong or had to look up.
2. Ask Claude for a 10-minute mock: "Design the driver matching for Uber" (or "Design Yelp's nearby search"), Claude as interviewer. Cover the trade-off of each stage without notes.
3. Not covered here: sharding locations by region (one Redis per city), quadtrees and Uber's H3 hexagons, and routing distance instead of straight-line distance (a driver across the river is not 500 m away).
