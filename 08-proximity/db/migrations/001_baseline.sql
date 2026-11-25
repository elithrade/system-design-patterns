-- The baseline schema. Add your own changes as new files (002_..., 003_...). The app applies files
-- it has not seen yet, in name order, each in a transaction, and records them in schema_migrations.

-- PostGIS is installed in the image; stage 2 uses it, and stage 1 may use ST_GeoHash.
CREATE EXTENSION IF NOT EXISTS postgis;

-- Where each driver is. Drivers report every few seconds.
CREATE TABLE drivers (
    id         bigint      PRIMARY KEY,
    lat        float8      NOT NULL,
    lng        float8      NOT NULL,
    available  boolean     NOT NULL,
    updated_at timestamptz NOT NULL
);

-- Great-circle distance in meters on a sphere with PostGIS's mean Earth radius, so it agrees with
-- ST_Distance(geography, geography, false). Geo.DistanceM in C# is the same formula.
CREATE FUNCTION haversine_m(lat1 float8, lng1 float8, lat2 float8, lng2 float8) RETURNS float8
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT 2 * 6371008.7714 * asin(sqrt(
        power(sin(radians(lat2 - lat1) / 2), 2) +
        cos(radians(lat1)) * cos(radians(lat2)) * power(sin(radians(lng2 - lng1) / 2), 2)))
$$;
