-- The whole schema for this lab: you write no migrations here (add one if you want an index).
-- The app applies files it has not seen yet, in name order, and records them in schema_migrations.

-- A video: its source upload and where it is on the way to published.
--   pending     created, waiting for the upload
--   uploaded    the source is in MinIO with the declared size
--   processing  renditions are being made
--   published   every rendition is done
--   failed      a rendition failed for good, or the upload was the wrong size
--   abandoned   the upload never came (stage 4)
CREATE TABLE videos (
    id           bigserial   PRIMARY KEY,
    owner_id     bigint      NOT NULL,
    title        text        NOT NULL,
    content_type text        NOT NULL,
    size         bigint      NOT NULL,
    status       text        NOT NULL DEFAULT 'pending'
                 CHECK (status IN ('pending', 'uploaded', 'processing', 'published', 'failed', 'abandoned')),
    reason       text,
    -- bumped by every change to the video or its renditions (triggers below): progress events
    -- carry it, so a watcher can tell a newer state from an older one
    version      bigint      NOT NULL DEFAULT 1,
    created_at   timestamptz NOT NULL
);

-- One row per rendition of a video (360p, 720p, thumb). With the queue engine (stage 2) each row
-- is also a job: the columns from attempts on are the queue's, as in lab 5.
CREATE TABLE renditions (
    video_id    bigint      NOT NULL REFERENCES videos (id),
    name        text        NOT NULL,
    status      text        NOT NULL DEFAULT 'queued' CHECK (status IN ('queued', 'running', 'done', 'failed')),
    percent     int         NOT NULL DEFAULT 0 CHECK (percent BETWEEN 0 AND 100),
    size        bigint,
    attempts    int         NOT NULL DEFAULT 0,
    run_after   timestamptz NOT NULL,
    locked_by   text,
    lease_until timestamptz,
    last_error  text,
    PRIMARY KEY (video_id, name)
);

CREATE FUNCTION videos_bump_version() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.version := OLD.version + 1;
    RETURN NEW;
END;
$$;
CREATE TRIGGER videos_version BEFORE UPDATE ON videos FOR EACH ROW EXECUTE FUNCTION videos_bump_version();

-- A rendition change bumps its video's version too (the UPDATE fires the trigger above). It also
-- locks the video row until the change commits, so versions follow commit order.
CREATE FUNCTION renditions_touch_video() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    UPDATE videos SET version = version WHERE id = NEW.video_id;
    RETURN NULL;
END;
$$;
CREATE TRIGGER renditions_touch AFTER INSERT OR UPDATE ON renditions FOR EACH ROW EXECUTE FUNCTION renditions_touch_video();
