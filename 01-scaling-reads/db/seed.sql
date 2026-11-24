-- Defines seed(...), which wipes the tables and fills them with generated data.
-- Not a migration: run it by hand (README.md, "Setup"). The tests call it at a small scale.
--
-- Posts 1..celebrities are the "celebrity" posts: each gets celebrity_likes likes, from users
-- 1..celebrity_likes. Every other like is a random (post, user) pair.
-- The same arguments always produce the same data (setseed).
CREATE OR REPLACE PROCEDURE seed(
    users           int,
    posts           int,
    random_likes    int,
    celebrities     int,
    celebrity_likes int)
LANGUAGE plpgsql AS $$
BEGIN
    IF celebrity_likes > users THEN
        RAISE EXCEPTION 'celebrity_likes (%) cannot exceed users (%)', celebrity_likes, users;
    END IF;

    TRUNCATE likes, posts, users RESTART IDENTITY;
    PERFORM setseed(0.42);

    INSERT INTO users (id, name, avatar_url)
    SELECT i, 'user ' || i, 'https://img.example/avatars/' || i || '.jpg'
    FROM generate_series(1, users) AS i;

    INSERT INTO posts (author_id, caption, created_at)
    SELECT 1 + floor(random() * users)::bigint,
           'post ' || i,
           now() - random() * interval '365 days'
    FROM generate_series(1, posts) AS i;

    INSERT INTO likes (post_id, user_id)
    SELECT p, u
    FROM generate_series(1, celebrities) AS p, generate_series(1, celebrity_likes) AS u;

    INSERT INTO likes (post_id, user_id)
    SELECT 1 + floor(random() * posts)::bigint, 1 + floor(random() * users)::bigint
    FROM generate_series(1, random_likes)
    ON CONFLICT DO NOTHING;

    -- Seeding inserts likes directly, so a denormalized count (stage 1) must be rebuilt here.
    -- If your stage 1 migration names the column something else, change it here too.
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'posts' AND column_name = 'like_count') THEN
        UPDATE posts p SET like_count = c.n
        FROM (SELECT post_id, count(*) AS n FROM likes GROUP BY post_id) AS c
        WHERE c.post_id = p.id;
    END IF;

    ANALYZE users;
    ANALYZE posts;
    ANALYZE likes;
END;
$$;
