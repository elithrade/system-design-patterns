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
- Each lab has its own `compose.yaml` with host ports that do not collide with other labs (lab 1: 55432, 55433, 56379, app 5100).
- Load tests are k6 scripts with every setting passed by `-e` and documented at the top of the script.

## Lab 1: `01-scaling-reads/`

- `src/ScalingReads/`: `Program.cs` (wiring by `Reads:Router` and `Reads:Cache`), `Endpoints.cs`, `Options.cs`, `Stats.cs` (counters behind `GET /stats`), `Data/` (`DataSources`, `PostQueries`, `PostRepository`, `Migrator`), `Routing/`, `Caching/` (`ICache`, `RedisCache`, `IPostReader`).
- Owner's stubs: `db/migrations/002_*.sql` and `Data/PostQueries.cs` (stage 1), `Routing/ReadYourWritesRouter.cs` (stage 2), `Caching/CachedPostReader.cs` (stage 3), `Caching/HotKeyPostReader.cs` (stage 4).
- `tests/ScalingReads.Tests/Infra/`: `InfraFixture` builds a template database (migrations + small seed) once and clones it per test; `App` is the `WebApplicationFactory` with settings and a `FakeTimeProvider`; `Concurrently.RunAsync` releases N tasks together.
- The compose replica is a real streaming replica delayed by `REPLICA_DELAY` (default 2s). In tests the "replica" is a second clone that never receives writes.
