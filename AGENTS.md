# AGENTS.md

Instructions for AI coding agents working in this repository.

## Project

Hands-on labs for the system design patterns in Hello Interview's "8 Patterns" video, one folder per pattern (see `README.md` for the roadmap and the learning loop). It is a **learning project**: the owner writes the code in the stub files personally.

- Never fill in a stub (a method that throws `NotImplementedException`) or commit a solution, even partially, unless the owner explicitly asks for that specific piece. Hints, reviews of the owner's code, and explanations are welcome.
- Do not weaken or delete a stage's tests to make the owner's code pass. If a test is wrong, say so and fix the test.
- Infrastructure, baseline code, tests, k6 scripts and docs are fair game.
- When building a new lab: write the baseline and the tests, verify in a scratch copy that reference solutions pass every stage and that the README's mutations each turn a test red, then delete the reference solutions. Do not leave them in the repo or its history.

## Conventions

- .NET 10, C#, one namespace per lab (`ScalingReads`), minimal APIs.
- Tests: xUnit v3 on Microsoft.Testing.Platform (root `global.json`), Testcontainers for Postgres/Redis, one namespace per stage so `dotnet test --filter-namespace "*StageN"` runs a stage. Inject `TimeProvider`; tests use `FakeTimeProvider`.
- Each lab has its own `compose.yaml` with host ports that do not collide with other labs (table below). A new port must not reuse one.
- Load tests are k6 scripts with every setting passed by `-e` and documented at the top of the script.
- Every lab has the same layout (`README.md` at the root describes it) and the same test infrastructure: `InfraFixture` builds a template database once and clones it per test (`CREATE DATABASE ... TEMPLATE`), `App` is the `WebApplicationFactory` with settings and a `FakeTimeProvider`, and baseline tests in the `Baseline` namespace must pass on the stubs.

## State (2026-11-25)

All nine labs are built and verified. The owner works through them in order; check `git log` and the stub files for progress before assuming a stage is untouched. On the stubs, each lab's baseline tests pass and every stage test fails:

| Lab | Namespace | Ports (compose; app) | Baseline green / stage red |
|---|---|---|---|
| `01-scaling-reads` | `ScalingReads` | Postgres 55432, replica 55433, Redis 56379; app 5100 | 4 / 23 |
| `02-scaling-writes` | `ScalingWrites` | Postgres 55532, partitions 55533-55535; app 5200 | 3 / 26 |
| `03-contention` | `Contention` | sales 55632, payments 55633, Redis 56579; app 5300 | 3 / 21 |
| `04-large-blobs` | `LargeBlobs` | Postgres 55732, MinIO 59000/59001; app 5400 | 1 / 27 |
| `05-long-running` | `LongRunning` | Postgres 55832, nginx 5580 (host network); app 5500 | 2 / 21 |
| `06-realtime` | `Realtime` | Postgres 55932, Redis 56879; app 5600 (second instance 5601) | 4 / 29 |
| `07-multi-step` | `MultiStep` | Postgres 56032, Temporal 57233, UI 58233; app 5700 | 3 / 33 |
| `08-proximity` | `Proximity` | PostGIS 56132, Redis 57079; app 5800 | 3 / 25 |
| `09-capstone` | `VideoPlatform` | Postgres 56232, MinIO 59100/59101, Temporal 57333, UI 58333; app 5900 (second instance 5901) | 3 / 32 |

Known issue, not fixed: lab 1's `Databases` registers instances, so DI does not dispose them. Mention it rather than changing it unless the owner asks.

## Owner's stubs, by lab

Each stub's class comment states what the tests check. Labs 1-8 have more detail in their READMEs.

- Lab 1: `db/migrations/002_*.sql`, `Data/PostQueries.cs` (stage 1), `Routing/ReadYourWritesRouter.cs` (2), `Caching/CachedPostReader.cs` (3), `Caching/HotKeyPostReader.cs` (4).
- Lab 2: `Counting/BatchingViewCounter.cs` (1), `db/migrations/002_*.sql` and `Counting/ShardedViewCounter.cs` (2), `Partitioning/ConsistentHashRing.cs` and `ScatterGatherTopVideos` in `Partitioning/TopVideos.cs` (3), `Queueing/ViewQueue.cs` (4).
- Lab 3: `Tickets/ConditionalTicketSeller.cs` (1), `Tickets/LockingTicketSeller.cs` (2), `db/migrations/002_*.sql` and `Seats/OptimisticSeatSeller.cs` (3), `Seats/SeatHolds.cs` (4). A last README section splits payments onto a second database; it has no stub.
- Lab 4: `Uploads/PresignedUploads.cs` (1), `Uploads/MultipartUploads.cs` (2), `Events/ObjectCreatedHandler.cs` (3), `Cleanup/OrphanSweeper.cs` (4), `Downloads/DownloadLinks.cs` (5).
- Lab 5: `db/migrations/002_*.sql` and `Jobs/JobStore.cs` (1), `ClaimAsync` in `Jobs/SkipLockedQueue.cs` (2), the rest of `SkipLockedQueue` (3), `ListDeadAsync`/`RequeueDeadAsync` (4).
- Lab 6: `Live/LongPoll.cs` (1), `Live/BidStream.cs` (2 and 5), `Live/BidSocket.cs` (3, tests on Kestrel), `Bus/RedisBidBus.cs` (4).
- Lab 7: `db/migrations/002_*.sql` and `Idempotency/IdempotencyStore.cs` (1), `ResumeAsync` in `Saga/SagaCheckout.cs` (2), `Saga/SagaRecovery.cs` (3), `Temporal/CheckoutWorkflow.cs` (4).
- Lab 8: `Geo/Geohash.cs`, `db/migrations/002_*.sql`, `Search/GeohashIndex.cs` (1), `db/migrations/003_*.sql`, `Search/PostGisIndex.cs` (2), `Search/RedisDriverIndex.cs` (3 and 4).
- Lab 9: `Uploads/VideoUploads.cs` (1), `Jobs/TranscodeQueue.cs` (2), `Live/ProgressStream.cs` and `Bus/PgProgressBus.cs` (3), `Temporal/PublishWorkflow.cs` (4). The schema is complete: the owner writes no migrations here.

`Stats.cs` in lab 2 and the Temporal wiring in labs 7 and 9 mention `NotImplementedException` in a catch or comment; they are not stubs.

## Things learned building the labs

- Npgsql reads `timestamptz` as `DateTime`: Dapper records need `DateTime`, not `DateTimeOffset`. Set `DefaultTypeMap.MatchNamesWithUnderscores = true`. Postgres `numeric` comes back as `decimal`; cast to `float8` for `double`.
- `Stats.Snapshot()` returns anonymous types, which are internal: tests read counters through public properties, not `dynamic`.
- Temporal: connect the client at startup (a lazy client made the worker stop the host). Tests share one dev server, and every test database numbers rows from 1, so workflow ids need a per-test prefix (lab 9's `Temporal:WorkflowIdPrefix`) or a leftover running workflow breaks later tests.
- Lab 8 runs Postgres with `jit=off`: the planner underestimates `ST_DWithin` rows, prices searches high, and JIT-compiles each one.
- k6: `__ITER` is per VU; use `exec.scenario.iterationInTest` for a global counter.
- Timing tests assert a ratio with a wide margin (lab 8: 5x required, 11-18x measured), never an absolute time.
- A README mutation is listed only if it was seen to turn a test red. Mutations no test catches (for example dropping `SKIP LOCKED` in lab 9, where blocked claimers return nothing) are left out.
- Stop processes with `pkill -x Name`; `pkill -f` matches the calling shell too.

## Lab 1: `01-scaling-reads/`

- `src/ScalingReads/`: `Program.cs` (wiring by `Reads:Router` and `Reads:Cache`), `Endpoints.cs`, `Options.cs`, `Stats.cs` (counters behind `GET /stats`), `Data/` (`DataSources`, `PostQueries`, `PostRepository`, `Migrator`), `Routing/`, `Caching/` (`ICache`, `RedisCache`, `IPostReader`).
- Owner's stubs: `db/migrations/002_*.sql` and `Data/PostQueries.cs` (stage 1), `Routing/ReadYourWritesRouter.cs` (stage 2), `Caching/CachedPostReader.cs` (stage 3), `Caching/HotKeyPostReader.cs` (stage 4).
- `tests/ScalingReads.Tests/Infra/`: `InfraFixture` builds a template database (migrations + small seed) once and clones it per test; `App` is the `WebApplicationFactory` with settings and a `FakeTimeProvider`; `Concurrently.RunAsync` releases N tasks together.
- The compose replica is a real streaming replica delayed by `REPLICA_DELAY` (default 2s). In tests the "replica" is a second clone that never receives writes.
