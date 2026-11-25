-- Defines seed(capacity, per_buyer_limit), which wipes the sales database and creates sale 1:
-- capacity general-admission tickets and capacity reserved seats (A1, A2, ... 10 per row).
-- Not a migration: run it by hand before each k6 run (README.md, "Setup"). The tests call it too.
CREATE OR REPLACE PROCEDURE seed(capacity int, per_buyer_limit int)
LANGUAGE plpgsql AS $$
BEGIN
    TRUNCATE seat_orders, seats, tickets, sales RESTART IDENTITY CASCADE;

    INSERT INTO sales (id, name, capacity, per_buyer_limit)
    VALUES (1, 'flash sale', capacity, per_buyer_limit);

    -- a version column (stage 3) starts at its default
    INSERT INTO seats (id, sale_id, label)
    SELECT i, 1, chr(65 + ((i - 1) / 10) % 26) || (1 + (i - 1) % 10)
    FROM generate_series(1, capacity) AS i;

    ANALYZE sales;
    ANALYZE tickets;
    ANALYZE seats;
END;
$$;
