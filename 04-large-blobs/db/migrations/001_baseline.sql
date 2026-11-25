-- File metadata. The bytes live in MinIO under files/{id}.
-- Add your own changes as new files (002_...). The app applies files it has not seen yet, in name
-- order, each in a transaction, and records them in schema_migrations.
CREATE TABLE files (
    id           bigserial   PRIMARY KEY,
    owner_id     bigint      NOT NULL,
    name         text        NOT NULL,
    content_type text        NOT NULL,
    -- the size the client declared; for a ready file, also the size in MinIO
    size         bigint      NOT NULL,
    -- pending: the row exists, the bytes may not. ready: the bytes are in MinIO.
    -- failed: the bytes arrived but were not what was declared.
    status       text        NOT NULL CHECK (status IN ('pending', 'ready', 'failed')),
    -- the multipart upload in progress (stage 2), if any
    upload_id    text,
    created_at   timestamptz NOT NULL,
    ready_at     timestamptz
);
CREATE INDEX ON files (status, created_at);
