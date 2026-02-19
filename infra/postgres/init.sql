CREATE SCHEMA IF NOT EXISTS orders;
CREATE SCHEMA IF NOT EXISTS integration;
CREATE SCHEMA IF NOT EXISTS limits;
CREATE SCHEMA IF NOT EXISTS notifications;

CREATE TABLE IF NOT EXISTS orders.orders (
    id UUID PRIMARY KEY,
    account_id UUID NOT NULL,
    symbol TEXT NOT NULL,
    side TEXT NOT NULL,
    quantity NUMERIC(18, 6) NOT NULL,
    price NUMERIC(18, 6) NOT NULL,
    status TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_orders_status ON orders.orders (status);
CREATE INDEX IF NOT EXISTS idx_orders_updated_at ON orders.orders (updated_at DESC);

CREATE TABLE IF NOT EXISTS integration.outbox_messages (
    id UUID PRIMARY KEY,
    aggregate_id UUID NOT NULL,
    type TEXT NOT NULL,
    payload JSONB NOT NULL,
    headers JSONB NOT NULL,
    status TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    published_at TIMESTAMPTZ NULL,
    retries INT NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_outbox_status_created ON integration.outbox_messages (status, created_at);

CREATE TABLE IF NOT EXISTS integration.processed_messages (
    consumer_name TEXT NOT NULL,
    message_id UUID NOT NULL,
    processed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (consumer_name, message_id)
);

CREATE TABLE IF NOT EXISTS integration.flow_events (
    id UUID PRIMARY KEY,
    order_id UUID NULL,
    correlation_id TEXT NOT NULL,
    service TEXT NOT NULL,
    stage TEXT NOT NULL,
    broker TEXT NULL,
    channel TEXT NULL,
    message_id UUID NULL,
    payload_snippet TEXT NULL,
    metadata JSONB NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_flow_events_order_created ON integration.flow_events (order_id, created_at);
CREATE INDEX IF NOT EXISTS idx_flow_events_correlation_created ON integration.flow_events (correlation_id, created_at);
CREATE INDEX IF NOT EXISTS idx_flow_events_stage_created_desc ON integration.flow_events (stage, created_at DESC);

CREATE TABLE IF NOT EXISTS limits.account_limits (
    account_id UUID PRIMARY KEY,
    reserved_amount NUMERIC(18, 6) NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS notifications.notifications (
    id UUID PRIMARY KEY,
    order_id UUID NOT NULL,
    correlation_id TEXT NOT NULL,
    channel TEXT NOT NULL,
    payload JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_notifications_order ON notifications.notifications (order_id, created_at DESC);
