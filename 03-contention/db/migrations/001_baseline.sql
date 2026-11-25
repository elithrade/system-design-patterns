-- The baseline schema. Add your own changes as new files (002_..., 003_...). The app applies files
-- it has not seen yet, in name order, each in a transaction, and records them in schema_migrations.
-- Never edit a file that has already been applied: write a new one.

-- A general-admission sale (stages 1 and 2): capacity tickets, at most per_buyer_limit per buyer.
CREATE TABLE sales (
    id              bigint PRIMARY KEY,
    name            text   NOT NULL,
    capacity        int    NOT NULL,
    sold            int    NOT NULL DEFAULT 0,
    per_buyer_limit int    NOT NULL
);

-- One row per ticket sold. Nothing in the schema stops more than capacity rows: that is the lab.
CREATE TABLE tickets (
    id         bigserial   PRIMARY KEY,
    sale_id    bigint      NOT NULL REFERENCES sales (id),
    buyer_id   bigint      NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ON tickets (sale_id, buyer_id);

-- Reserved seats (stages 3 and 4). buyer_id is whoever bought it last.
CREATE TABLE seats (
    id       bigint PRIMARY KEY,
    sale_id  bigint NOT NULL REFERENCES sales (id),
    label    text   NOT NULL,
    status   text   NOT NULL DEFAULT 'available' CHECK (status IN ('available', 'sold')),
    buyer_id bigint
);
CREATE INDEX ON seats (sale_id, status, id);

-- The app adds a row here for every purchase it reports as successful, so a seat sold twice
-- shows up as two rows (GET /sales/{id} counts them).
CREATE TABLE seat_orders (
    id         bigserial   PRIMARY KEY,
    seat_id    bigint      NOT NULL REFERENCES seats (id),
    buyer_id   bigint      NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ON seat_orders (seat_id);
