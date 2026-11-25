-- The baseline schema. Add your own changes as new files (002_..., 003_...). The app applies files
-- it has not seen yet, in name order, each in a transaction, and records them in schema_migrations.

-- Three "services" share this database to keep the lab small. Treat their tables as remote: the
-- checkout code reaches them only through Services/*.cs, which can fail like a network call.

CREATE TABLE products (
    sku       text   PRIMARY KEY,
    price     bigint NOT NULL,
    available int    NOT NULL CHECK (available >= 0),
    -- what seed() stocked; available + held reservations should always add up to it
    stocked   int    NOT NULL
);

-- The payment service. Like Stripe, it takes an idempotency key: the same key charges once.
CREATE TABLE payments (
    id              bigserial   PRIMARY KEY,
    idempotency_key text        NOT NULL UNIQUE,
    order_id        bigint      NOT NULL,
    amount          bigint      NOT NULL,
    status          text        NOT NULL CHECK (status IN ('charged', 'refunded')),
    created_at      timestamptz NOT NULL
);

-- The inventory service.
CREATE TABLE reservations (
    id              bigserial   PRIMARY KEY,
    idempotency_key text        NOT NULL UNIQUE,
    order_id        bigint      NOT NULL,
    sku             text        NOT NULL REFERENCES products (sku),
    quantity        int         NOT NULL,
    status          text        NOT NULL CHECK (status IN ('held', 'released')),
    created_at      timestamptz NOT NULL
);

-- The shipping service.
CREATE TABLE shipments (
    id              bigserial   PRIMARY KEY,
    idempotency_key text        NOT NULL UNIQUE,
    order_id        bigint      NOT NULL,
    address         text        NOT NULL,
    created_at      timestamptz NOT NULL
);

-- The checkout's own state. op_key identifies one checkout attempt end to end: the services'
-- idempotency keys are derived from it.
CREATE TABLE orders (
    id             bigserial   PRIMARY KEY,
    op_key         text        NOT NULL UNIQUE,
    -- the client's own reference for the purchase; the audit groups retries by it
    client_ref     text        NOT NULL,
    customer_id    bigint      NOT NULL,
    sku            text        NOT NULL,
    quantity       int         NOT NULL,
    card           text        NOT NULL,
    address        text        NOT NULL,
    amount         bigint      NOT NULL,
    -- running: in progress. compensating: undoing earlier steps. completed / cancelled: finished.
    -- failed: the baseline gave up halfway.
    status         text        NOT NULL CHECK (status IN ('running', 'compensating', 'completed', 'cancelled', 'failed')),
    reason         text,
    charge_id      bigint,
    reservation_id bigint,
    shipment_id    bigint,
    -- stage 3: who may work on the order until when
    lease_until    timestamptz,
    created_at     timestamptz NOT NULL,
    updated_at     timestamptz NOT NULL
);
CREATE INDEX ON orders (status, lease_until);
CREATE INDEX ON orders (client_ref);
