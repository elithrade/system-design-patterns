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

`CREATE INDEX` can run inside `BEGIN; ... ROLLBACK;` to test a plan without keeping the index. Roll back promptly: while the transaction is open it holds the lock on `posts`, every insert and update on `posts` blocks, and other sessions can't see the index. `pg_stat_activity` shows the session as `idle in transaction`.
