-- The payments database (the last section of the README). It knows ticket ids, but it is a
-- different database: no foreign key can reach the tickets table.
CREATE TABLE payments (
    id         bigserial   PRIMARY KEY,
    ticket_id  bigint      NOT NULL,
    buyer_id   bigint      NOT NULL,
    amount     numeric     NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
