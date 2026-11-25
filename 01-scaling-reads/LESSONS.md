# Lessons: lab 1

## Indexing

### The table without an index

Postgres stores a table's rows in a heap: 8 kB pages in roughly insert order, sorted by nothing. `posts` is 130 MB, 16,667 pages of about 120 rows each.

To answer `WHERE author_id = 5` from the heap alone, Postgres reads every page and checks every row. User 5 has 11 posts, and their ids are scattered (513861, 1542567, 1914393...), so they sit on 11 different pages across the table. Nothing in the heap says which ones.

### What an index is

An index is a separate structure on disk. Each entry holds the indexed column values and a pointer to the row in the heap (page and slot). Entries are kept sorted and packed into 8 kB leaf pages, a few hundred per page. Above the leaves sits a small tree of signpost pages, each saying which value range lives in which page below. Leaves link to their neighbours, so a scan can read across without climbing back up.

Postgres uses a B-tree by default. For 2M entries the tree is 3 levels deep: one root, about 30 internal pages, about 8,000 leaves.

### A lookup, page by page

The profile query with an index on `posts (author_id, created_at DESC)`:

1. Read the root page: which range holds author 5. (1 page)
2. Read the internal page: which leaf holds author 5. (1 page)
3. Read the leaf from the first `(5, ...)` entry forward. Entries are already newest first. Stop at author 6 or after `LIMIT` entries. (1-2 pages)
4. Follow each entry's pointer to the heap for the columns the index doesn't hold, like `caption`. (11 pages)

About 15 pages instead of 16,667. Measured: 24.6 ms down to 0.099 ms. The speed-up (250x) is smaller than the page ratio (1,100x) because every query has fixed costs like parsing, planning and sending results.

### Column order

Design the index from the query it serves:

```sql
WHERE p.author_id = 5          -- equality filter: first column
ORDER BY p.created_at DESC     -- result order: second column
LIMIT 20                       -- can stop early only if the index is already in that order
```

A multi-column B-tree sorts by the first column, then by the second within equal values of the first. Phone book sorted by surname, then first name: all the Smiths are together and already in first-name order.

- `(author_id, created_at)`: author 5's posts are one contiguous block, newest first. No sort, stop after 20.
- `(created_at, author_id)`: sorted by time across all users, author 5's posts scattered through it. Nearly as bad as a seq scan.
- `(author_id)` alone: finds the posts fast, but Postgres fetches all of them and sorts. Fine for 11 posts, wasteful for 50,000.

Rule: columns filtered with `=` first, then the column you sort by.

`DESC` is optional here. Postgres can read a B-tree backwards (`Index Scan Backward`). It matters only for mixed orders like `ORDER BY a ASC, b DESC`.

### Several indexes on one table

A table can have many indexes, each a separate sorted copy of some columns pointing at the same rows. `posts` will have two:

- `posts_pkey`, sorted by `id`, serves `GET /posts/{id}`
- the author/created index, serves the profile page

The heap keeps its insert order. Each index adds one more sorted way in. Each one also adds work to every write, so add indexes for queries you run, not for every order you might want.

### Keeping an index up to date

`CREATE INDEX` builds the whole thing once: read every row, sort, write every leaf. On 2M rows it takes a few seconds and locks the table against writes while it runs.

After that Postgres maintains it entry by entry, inside the same transaction as the write, so the index can never disagree with the table:

- **Insert:** walk down to the right leaf (same 3 pages as a lookup), put the entry in sorted position. If the leaf is full, split it into two and add a signpost above. Cost grows with tree depth (O(log n)), not table size.
- **Delete:** the entry stays, pointing at a dead row, until `VACUUM` cleans it up. Lookups skip it.
- **Update:** Postgres never overwrites a row; it writes a new version. If an indexed column changed, every index gets a new entry. If no indexed column changed and the page has room, it's a HOT (heap-only tuple) update and the indexes aren't touched.

`REINDEX` is maintenance for bloated or damaged indexes, not something to run after writes.

Consequence for stage 1: every like updates `posts.like_count`. `like_count` is in no index, so those updates can be HOT. Indexing `like_count` would make every like write an index entry too.

### What an index can't fix

An index makes **finding** rows cheap. It can't make **computing** over them cheap. The like count already uses an index (`likes_pkey` on `(post_id, user_id)`), which jumps straight to a post's likes, but `count(*)` still steps through every entry: 2 for an ordinary post, 150,000 for post 1. Only storing the count (denormalization) makes it constant.

### Primary key, index, hash

- **Primary key** is a constraint: unique and not null. Postgres enforces it by building a unique index.
- **Index** is any structure that finds rows without scanning the table. Any column can have one, not only the key. A foreign key does **not** get one automatically in Postgres (`posts.author_id` had none).
- **B-tree vs hash** are two ways to build an index. `\d posts` shows `"posts_pkey" PRIMARY KEY, btree (id)`. A hash index is close to O(1) but only answers `=`, because hashing scatters neighbouring values. A B-tree is O(log n), and since each page holds hundreds of entries the tree stays 3 levels deep for millions of rows. It also answers ranges, `ORDER BY`, and the leading columns of a multi-column index. The profile query needs order, so only a B-tree helps.

### In EF Core

- EF creates an index on every foreign key property by convention, which is why missing FK indexes may never have come up. It would give `(author_id)` alone, not the composite.
- The composite needs `modelBuilder.Entity<Post>().HasIndex(p => new { p.AuthorId, p.CreatedAt })`. EF can't infer an index from LINQ.
- `p.Likes.Count()` in a `Select` becomes the same correlated subquery as the baseline `UserPosts`. See the SQL with `.ToQueryString()` or `LogTo(Console.WriteLine)`, then paste it into `EXPLAIN ANALYZE`.

## Denormalization: the stored like count

### Why

Counting likes on every read costs one index entry per like: about 2 for an ordinary post, 150,000 for post 1. A `like_count` column on `posts` makes every read cost the same. The price is two copies of one fact, and my SQL now keeps them in agreement instead of Postgres.

### The migration

`db/migrations/002_profile_index_like_count.sql`: the index, then the column, then the backfill.

```sql
ALTER TABLE posts ADD COLUMN like_count bigint NOT NULL DEFAULT 0;

UPDATE posts p
SET like_count = c.n
FROM (
    SELECT post_id, count(*) AS n
    FROM likes
    GROUP BY post_id
) c
WHERE c.post_id = p.id;
```

- `bigint` because `count(*)` returns `bigint` and the C# side reads a `long`.
- `NOT NULL` needs a `DEFAULT`, or the existing rows would have no value. Since Postgres 11, adding a column with a constant default is instant: Postgres records the default once and doesn't rewrite the table.
- The backfill reads inside out. The subquery makes one `(post_id, n)` row per post with likes (LINQ: `GroupBy(l => l.PostId).Select(g => new { g.Key, N = g.Count() })`). `UPDATE ... FROM` joins `posts` to it. Posts with no likes have no row in `c`, so they're skipped and keep the default 0.
- The `UPDATE` writes a new version of every matched row (Postgres never overwrites), so it takes a few seconds on 2M posts.
- In every grouped query, each selected column must be grouped or aggregated. Selecting `post_id` while grouping by `author_id` is an error.
- EF equivalents: `migrationBuilder.AddColumn<long>(...)` for the column, `migrationBuilder.Sql("...")` for the backfill, since LINQ can't express it.

### How the migrator applies it

- At startup, the app runs each `db/migrations/*.sql` file not listed in `schema_migrations`, in name order, each in its own transaction. A failure rolls back the whole file and the app doesn't start.
- Files are tracked **by name only**. Editing a file after it's been applied changes nothing on that database. Write a new file (`003_...`).
- Don't put `BEGIN`/`COMMIT` in a migration. The migrator already wraps the file.
- `dotnet test` builds a fresh database from the migration files on every run, so edit freely until the tests pass, and start the app against compose only when the file is final.
- Applying a migration by hand (outside the migrator) leaves it unrecorded, and the next app start fails on `relation already exists`. The fix was dropping what I'd created and letting the migrator apply the file.

### The like statement

```sql
WITH new_like AS (
    INSERT INTO likes (post_id, user_id) VALUES (@postId, @userId)
    ON CONFLICT DO NOTHING RETURNING post_id
)
UPDATE posts SET like_count = like_count + 1
WHERE id IN (SELECT post_id FROM new_like)
```

- `ON CONFLICT DO NOTHING` skips a duplicate like without an error, so from outside a new like and a duplicate look the same.
- `RETURNING post_id` hands back the inserted rows: one for a new like, zero for a duplicate. `CreatePost` uses `RETURNING` to get the new id, and EF does the same behind `SaveChanges`.
- `WITH name AS (...)` names one statement's result so the next can read it. The `UPDATE` only touches posts listed in `new_like`, so a duplicate updates nothing.
- One statement, so the insert and the increment commit together or not at all.
- Postgres reports the rows affected by the final `UPDATE`: `UPDATE 1` for a new like, `UPDATE 0` for a duplicate. Dapper's `ExecuteAsync` returns that number, so `PostRepository.LikeAsync`'s `inserted == 1` still works. With several separate statements in one command, `ExecuteAsync` returns the **sum**.
- Checked in psql: first run `UPDATE 1` and count 2 → 3, second run `UPDATE 0` and count stays 3.

The other route: a transaction in C#, run the insert, run the update only if the insert returned 1. Two round trips, decision in C#.

### Concurrency

`SET like_count = like_count + 1` is computed by Postgres while it holds the row lock. A second concurrent like waits for the lock, then adds 1 to the already-updated value. 100 simultaneous likes end at 100.

The EF-style read-modify-write loses updates:

```csharp
var post = await db.Posts.FindAsync(postId);   // both requests read 100
post.LikeCount++;                               // both compute 101
await db.SaveChangesAsync();                    // both write 101: one like lost, no error
```

EF fixes: a concurrency token (Npgsql can use the `xmin` system column) that makes the second save throw `DbUpdateConcurrencyException` so you retry, or `ExecuteUpdateAsync(s => s.SetProperty(p => p.LikeCount, p => p.LikeCount + 1))`, which sends the atomic `UPDATE`. A duplicate `Like` row makes `SaveChanges` throw `DbUpdateException`. LINQ has no `ON CONFLICT DO NOTHING`, so you catch the exception or use raw SQL.

### Reads

- Read the column straight off the row the query already has: `p.like_count`. A subquery like `(SELECT like_count FROM posts WHERE id = p.id)` returns the right number but looks up the same row again through `posts_pkey`, once per post.
- `like_count` is in no index, so the update on every like can be a HOT update and leaves both indexes alone.

### What it costs

- Every like writes two rows: the `likes` row and the `posts` row.
- Every like on the same post updates the same row and queues on its lock. Fine at this lab's rates. A celebrity post taking thousands of likes a second makes that row the bottleneck (lab 2).

## Load test: why cheap requests were slow

Baseline, `k6 run -e RATE=300 k6/reads.js`:

| profile p99 | post p99 | like p99 | dropped | completed |
|---|---|---|---|---|
| 5.84 s | 5.4 s | 5.41 s | 1,218 | 7,783 (218/s of 300/s asked) |

A post read is a primary-key lookup under 1 ms, yet its p99 matched the profile page's. The query wasn't slow. The request waited for a connection:

- The pool allows 50 connections to the primary (`Maximum Pool Size=50` in `appsettings.json`).
- Little's law: requests in flight = throughput × time each takes. 218/s × 2.84 s ≈ 620 in the app at once: 50 running, about 570 queued.
- The queue is first come, first served, so a 1 ms post read waits behind dozens of profile scans.
- About 107 profile requests a second × about 66 ms of CPU each ≈ 7 CPU-seconds per second, on 8 cores shared with the replica, the app and k6. Postgres was CPU-bound, so each scan ran slower and held its connection longer.

Run-to-run numbers vary with machine load and how much of the table is in memory. Compare runs on the same machine with the same command.

## Why this order: queries, then replicas, then caches

Each step adds read capacity and another copy of the data to keep correct:

| Step | Copy it adds | Who keeps it correct |
|---|---|---|
| Index | sorted copy of some columns | Postgres, same transaction, can't go wrong |
| Denormalized count | stored derived value | my SQL |
| Read replica (stage 2) | whole database, seconds behind | replication; users can see stale data |
| Cache (stages 3, 4) | a second store with its own expiry | my invalidation code |

A cache in front of the unfixed query helps less than it looks:

- Most profiles are rarely read. Those reads miss the cache and run the query, and without the index every miss is a 2M-row scan.
- When the cache restarts or evicts, all the misses land on the database at once.
- A replica runs the same plan, so replicas multiply a bad query's cost along with the capacity.

After stage 1 a miss costs 0.1 ms instead of 24 ms. The cache decides how often the database is asked; the query decides what each ask costs.

The tool names are the easy part. The trade-offs come from the details: duplicate likes, concurrent likes, rows affected, the hot row lock, the lock `CREATE INDEX` takes.

## Reading query plans

- `EXPLAIN <query>` plans only, prints estimates, runs nothing.
- `EXPLAIN ANALYZE <query>` runs the query and adds actual numbers. It executes writes for real: wrap them in `BEGIN; ... ROLLBACK;`.
- `EXPLAIN (ANALYZE, BUFFERS)` adds pages read per node (`shared hit` from memory, `read` from disk). Page counts don't change with machine load; milliseconds do.

Reading a node:

- `cost=a..b` is the planner's estimate in arbitrary units: startup cost, total cost. Not milliseconds.
- `actual time=a..b rows=n loops=k` is what happened: ms to first row, ms to last row, rows produced. Time and rows are **per loop**; multiply by `loops`.
- Read from the most indented lines outward. Inner nodes run first and pass rows up.
- Estimated rows far from actual rows means the planner chose a plan for a table that doesn't match reality.

What the baseline profile plan showed:

| Node | Meaning |
|---|---|
| `Parallel Seq Scan on posts`, `loops=3`, `Rows Removed by Filter: 666663` | 3 processes read ~2M rows to keep 11. 22 ms of 24.6 ms. |
| `Sort`, `Gather Merge` | sort each worker's rows, merge the streams |
| `Index Scan using users_pkey`, `Index Cond: (id = 5)` | Postgres inferred `u.id = 5` from the join and the filter |
| `Materialize`, `loops=11` | keeps the one user row for all 11 posts |
| `SubPlan 1`, `Index Only Scan using likes_pkey`, `loops=11`, `Heap Fetches: 0` | like count per post, answered from the index alone |

With the index: one `Index Scan using posts_author_created_idx`, `rows=11`, no rows removed, no `Sort`, no workers. The `SubPlan` went from 0.2% of the query to about half of it.

Parallelism hides cost: 22 ms wall time across 3 processes is about 66 ms of CPU per request. Under load that's what saturates the machine and the connection pool.

## psql

| | |
|---|---|
| `\dt`, `\di` | list tables, list indexes |
| `\d posts` | columns, indexes, constraints |
| `\timing` | print each statement's time |
| `\pset pager off` | keep long output in the terminal instead of `less` |
| Ctrl+C or `\r` | discard a half-typed statement |
| `\q` | quit |

A statement runs only at `;`. A prompt of `feed-#` instead of `feed=#` means psql is waiting for the rest.

The prompt shows the transaction state: `feed=#` none open, `feed=*#` inside a transaction, `feed=!#` inside a failed one. After an error inside a transaction, Postgres rejects every statement until `ROLLBACK`. Without `BEGIN`, psql commits each statement on its own, and a later `ROLLBACK` has nothing to undo.

`CREATE INDEX` can run inside `BEGIN; ... ROLLBACK;` to test a plan without keeping the index. Roll back promptly: while the transaction is open it holds the lock on `posts`, every insert and update on `posts` blocks, and other sessions can't see the index. `pg_stat_activity` shows the session as `idle in transaction`. `ALTER TABLE` takes a stricter lock that blocks reads as well.

### Testing a migration file

Compose mounts `db/` inside the Postgres container at `/db`, and edits show up immediately. Edit the file in the editor, then type each line separately:

```
BEGIN;
\i /db/migrations/002_profile_index_like_count.sql
SELECT id, like_count FROM posts WHERE id IN (1, 11, 1000000);
ROLLBACK;
```

A backslash command like `\i` takes the rest of its line as arguments. Pasting several lines at once can arrive as one line, and then `\i` swallows the `SELECT` and `ROLLBACK` as ignored arguments and the transaction stays open. Check the prompt for `*` afterwards.

The tag after a statement is a quick check: `UPDATE 199992` was the number of users with posts, which showed the file still had the example statement instead of mine.
