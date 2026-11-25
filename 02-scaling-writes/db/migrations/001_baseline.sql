-- The baseline schema: one row per video, and its view count on that row.
-- Add your own changes as new files (002_..., 003_...). The app applies files it has not seen yet,
-- in name order, each in a transaction, and records them in schema_migrations. Never edit a file
-- that has already been applied: write a new one.

CREATE TABLE videos (
    id    bigint PRIMARY KEY,
    title text   NOT NULL,
    -- every view runs UPDATE ... SET views = views + 1 on this row, so a popular video's views
    -- queue up behind one row lock
    views bigint NOT NULL DEFAULT 0
);
