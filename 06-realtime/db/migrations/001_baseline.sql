-- The baseline schema. The app applies files it has not seen yet, in name order, each in a
-- transaction, and records them in schema_migrations. This lab needs no migrations of yours.

CREATE TABLE auctions (
    id            bigint      PRIMARY KEY,
    title         text        NOT NULL,
    ends_at       timestamptz NOT NULL,
    current_price bigint      NOT NULL,
    leader_id     bigint
);

-- Every accepted bid. The id is the event's sequence number: clients say "bids after 41".
CREATE TABLE bids (
    id         bigserial   PRIMARY KEY,
    auction_id bigint      NOT NULL REFERENCES auctions (id),
    bidder_id  bigint      NOT NULL,
    amount     bigint      NOT NULL,
    placed_at  timestamptz NOT NULL
);
CREATE INDEX ON bids (auction_id, id);
