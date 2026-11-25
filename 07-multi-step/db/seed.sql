-- Defines seed(stock), which wipes every table and stocks three products. Not a migration: run it
-- by hand (README.md, "Setup"). 'scarce' has 20 units whatever you pass, so it sells out.
CREATE OR REPLACE PROCEDURE seed(stock int)
LANGUAGE plpgsql AS $$
BEGIN
    TRUNCATE orders, shipments, reservations, payments, products RESTART IDENTITY CASCADE;
    INSERT INTO products (sku, price, available, stocked) VALUES
        ('mug', 1200, stock, stock),
        ('poster', 2500, stock, stock),
        ('scarce', 9900, 20, 20);
END;
$$;
