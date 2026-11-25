-- Defines seed(auctions), which wipes the data and creates that many auctions, each at 100 and
-- ending 30 days from now. Not a migration: run it by hand (README.md, "Setup").
CREATE OR REPLACE PROCEDURE seed(auctions int)
LANGUAGE plpgsql AS $$
BEGIN
    TRUNCATE bids, auctions RESTART IDENTITY;
    INSERT INTO auctions (id, title, ends_at, current_price)
    SELECT i, 'lot ' || i, now() + interval '30 days', 100 FROM generate_series(1, auctions) AS i;
END;
$$;
