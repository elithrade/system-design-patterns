-- Defines seed(users, activities_per_user), which wipes the data and the job queue and fills them
-- with generated data for 2026. Not a migration: run it by hand (README.md, "Setup").
-- The tests call it at a small scale. The same arguments always produce the same data.
CREATE OR REPLACE PROCEDURE seed(users int, activities_per_user int)
LANGUAGE plpgsql AS $$
BEGIN
    TRUNCATE jobs, activities, users RESTART IDENTITY CASCADE;
    PERFORM setseed(0.42);

    INSERT INTO users (id, name)
    SELECT i, 'runner ' || i FROM generate_series(1, users) AS i;

    INSERT INTO activities (user_id, kind, started_at, distance_m, seconds)
    SELECT u,
           (ARRAY['run', 'ride', 'swim'])[1 + floor(random() * 3)::int],
           timestamptz '2026-01-01' + random() * interval '365 days',
           1000 + floor(random() * 20000)::int,
           600 + floor(random() * 6000)::int
    FROM generate_series(1, users) AS u, generate_series(1, activities_per_user);

    ANALYZE users;
    ANALYZE activities;
END;
$$;
