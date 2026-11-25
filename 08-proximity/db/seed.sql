-- Defines seed(drivers), which replaces every driver with that many at random points around New
-- York City (a box of about 39 x 30 km); 80% are available. Not a migration: run it by hand
-- (README.md, "Setup"). The same argument always gives the same drivers.
CREATE OR REPLACE PROCEDURE seed(drivers int)
LANGUAGE plpgsql AS $$
BEGIN
    TRUNCATE drivers;
    PERFORM setseed(0.8);
    INSERT INTO drivers (id, lat, lng, available, updated_at)
    SELECT i, 40.55 + random() * 0.35, -74.10 + random() * 0.35, random() < 0.8, now()
    FROM generate_series(1, drivers) AS i;
    ANALYZE drivers;
END;
$$;
