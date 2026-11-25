-- Defines seed(videos), which wipes the main database's tables and fills them with generated data.
-- Not a migration: run it by hand (README.md, "Setup"). The tests call it at a small scale.
-- The same argument always produces the same data (setseed).
CREATE OR REPLACE PROCEDURE seed(videos int)
LANGUAGE plpgsql AS $$
BEGIN
    -- Stage 2 keeps views in video_view_shards as well. Start it empty.
    IF to_regclass('video_view_shards') IS NOT NULL THEN
        TRUNCATE video_view_shards;
    END IF;
    -- CASCADE: also empties any table of yours that references videos
    TRUNCATE videos CASCADE;
    PERFORM setseed(0.42);

    INSERT INTO videos (id, title, views)
    SELECT i, 'video ' || i, floor(random() * 10000)::bigint
    FROM generate_series(1, videos) AS i;

    ANALYZE videos;
END;
$$;
