-- The baseline schema. Add your own changes as new files (002_..., 003_...). The app applies files
-- it has not seen yet, in name order, each in a transaction, and records them in schema_migrations.

CREATE TABLE users (
    id   bigint PRIMARY KEY,
    name text   NOT NULL
);

-- What a year-in-review summarizes.
CREATE TABLE activities (
    id         bigserial   PRIMARY KEY,
    user_id    bigint      NOT NULL REFERENCES users (id),
    kind       text        NOT NULL,
    started_at timestamptz NOT NULL,
    distance_m int         NOT NULL,
    seconds    int         NOT NULL
);
CREATE INDEX ON activities (user_id, started_at);

-- The job queue: one row per job, in the same database as the data.
CREATE TABLE jobs (
    id          bigserial   PRIMARY KEY,
    -- 'year-in-review' (slow) or 'summary' (fast)
    kind        text        NOT NULL,
    user_id     bigint      NOT NULL,
    year        int         NOT NULL,
    -- queued: waiting to run (when run_after has passed). running: a worker holds it until
    -- lease_until. done: result is set. failed: gave up (the baseline). dead: gave up after
    -- max attempts and waits for a person (stage 4).
    status      text        NOT NULL CHECK (status IN ('queued', 'running', 'done', 'failed', 'dead')),
    attempts    int         NOT NULL DEFAULT 0,
    run_after   timestamptz NOT NULL,
    locked_by   text,
    lease_until timestamptz,
    last_error  text,
    result      bytea,
    created_at  timestamptz NOT NULL,
    finished_at timestamptz
);
CREATE INDEX ON jobs (status, run_after, id);
