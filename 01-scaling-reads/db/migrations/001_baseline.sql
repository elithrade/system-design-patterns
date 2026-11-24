-- The baseline schema: correct, and slow on purpose.
-- Add your own changes as new files (002_..., 003_...). The app applies files it has not seen yet,
-- in name order, each in a transaction, and records them in schema_migrations. Never edit a file
-- that has already been applied: write a new one.

CREATE TABLE users (
    id         bigint PRIMARY KEY,
    name       text   NOT NULL,
    avatar_url text   NOT NULL
);

-- No index on author_id: Postgres does not create one for a foreign key.
CREATE TABLE posts (
    id         bigserial   PRIMARY KEY,
    author_id  bigint      NOT NULL REFERENCES users (id),
    caption    text        NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- One row per like. The primary key stops a user liking a post twice.
CREATE TABLE likes (
    post_id    bigint      NOT NULL REFERENCES posts (id),
    user_id    bigint      NOT NULL REFERENCES users (id),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (post_id, user_id)
);
