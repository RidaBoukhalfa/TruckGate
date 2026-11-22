-- V004: exit scans and gate receipts, supervisor review and labels, alert delivery,
-- phone sync (photos), payment batches.

-- ------------------------------------------------------------------ gate: exit without receipt

ALTER TABLE tg.gate_scans DROP CONSTRAINT gate_scans_scan_type_check;
ALTER TABLE tg.gate_scans ADD CONSTRAINT gate_scans_scan_type_check
    CHECK (scan_type IN ('QUARRY_TICKET', 'GATE_RECEIPT', 'MANUAL_CODE', 'MANUAL_SELECT'));

-- A review can close a trip that already left, and a sample review can reopen a closed trip.
INSERT INTO tg.trip_transitions (from_state, to_state)
VALUES ('PENDING_REVIEW', 'CLOSED'),
       ('CLOSED', 'PENDING_REVIEW');

-- Every printed receipt (reprints included) is recorded.
CREATE TABLE tg.receipt_prints
(
    id         bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    trip_id    bigint      NOT NULL REFERENCES tg.trips (id),
    printed_at timestamptz NOT NULL DEFAULT now(),
    printed_by integer REFERENCES tg.users (id)
);
CREATE INDEX ON tg.receipt_prints (trip_id);

-- ------------------------------------------------------------------ labels for later AI training

CREATE TABLE tg.snapshot_labels
(
    id          bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    snapshot_id bigint      NOT NULL REFERENCES tg.snapshots (id),
    label_type  text        NOT NULL CHECK (label_type IN ('LOAD', 'PLATE', 'QUALITY')),
    value       text        NOT NULL,
    labeled_by  integer REFERENCES tg.users (id),
    labeled_at  timestamptz NOT NULL DEFAULT now(),
    CHECK (label_type <> 'LOAD' OR value IN ('FULL', 'PARTIAL', 'EMPTY', 'NOT_VISIBLE')),
    CHECK (label_type <> 'QUALITY' OR value IN ('GOOD', 'BLURRED', 'DARK', 'GLARE', 'OBSTRUCTED')),
    CHECK (label_type <> 'PLATE' OR length(value) BETWEEN 1 AND 32)
);
-- the latest label of a type wins; older ones stay as history
CREATE INDEX ON tg.snapshot_labels (snapshot_id, label_type, id DESC);

-- One random sample per day, never twice.
CREATE TABLE tg.review_samples
(
    day        date PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now(),
    trip_count integer     NOT NULL,
    percent    numeric     NOT NULL
);

-- ------------------------------------------------------------------ alert delivery

CREATE TABLE tg.alert_deliveries
(
    id           bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    alert_id     bigint      NOT NULL REFERENCES tg.alerts (id),
    channel      text        NOT NULL CHECK (channel IN ('SMS', 'TELEGRAM')),
    recipient    text        NOT NULL,
    attempted_at timestamptz NOT NULL DEFAULT now(),
    ok           boolean     NOT NULL,
    error        text
);
CREATE INDEX ON tg.alert_deliveries (alert_id, channel);

-- Channel secrets (Telegram bot token), encrypted by the server key file; never copied to the audit log.
CREATE TABLE tg.channel_secrets
(
    key        text PRIMARY KEY,
    value_enc  bytea       NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);

-- Watchdog: an alert of a given type is raised once per entity and key (e.g. once per day).
CREATE TABLE tg.alert_once
(
    type      text        NOT NULL,
    entity_id text        NOT NULL,
    period    text        NOT NULL,
    alert_id  bigint      NOT NULL REFERENCES tg.alerts (id),
    PRIMARY KEY (type, entity_id, period)
);

-- ------------------------------------------------------------------ phone sync

-- Photo of the loaded truck taken on the phone; its SHA-256 is inside the signed ticket.
CREATE TABLE tg.ticket_photos
(
    device_id   integer     NOT NULL,
    seq         bigint      NOT NULL,
    path        text        NOT NULL,
    sha256      bytea       NOT NULL CHECK (length(sha256) = 32),
    size_bytes  integer     NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (device_id, seq),
    FOREIGN KEY (device_id, seq) REFERENCES tg.tickets (device_id, seq)
);

-- One VOIDED event per ticket.
CREATE UNIQUE INDEX ticket_events_one_void ON tg.ticket_events (device_id, seq) WHERE type = 'VOIDED';

-- ------------------------------------------------------------------ payment

CREATE TABLE tg.payment_batches
(
    id             bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    period_from    date        NOT NULL,
    period_to      date        NOT NULL,
    price_per_trip numeric(14, 2) NOT NULL CHECK (price_per_trip >= 0),
    partial_percent numeric(5, 2) NOT NULL CHECK (partial_percent BETWEEN 0 AND 100),
    currency       text        NOT NULL,
    trip_count     integer     NOT NULL,
    total          numeric(16, 2) NOT NULL,
    note           text,
    created_by     integer REFERENCES tg.users (id),
    created_at     timestamptz NOT NULL DEFAULT now(),
    CHECK (period_to >= period_from)
);

CREATE TABLE tg.payment_items
(
    batch_id    bigint         NOT NULL REFERENCES tg.payment_batches (id),
    trip_id     bigint         NOT NULL UNIQUE REFERENCES tg.trips (id),
    driver_id   integer        NOT NULL,
    truck_id    integer        NOT NULL,
    load_status text,
    amount      numeric(14, 2) NOT NULL,
    PRIMARY KEY (batch_id, trip_id)
);

-- ------------------------------------------------------------------ guards and audit

DO
$$
DECLARE
    t text;
BEGIN
    -- append-only evidence
    FOREACH t IN ARRAY ARRAY ['receipt_prints', 'snapshot_labels', 'review_samples', 'alert_deliveries',
                              'alert_once', 'ticket_photos', 'payment_batches', 'payment_items'] LOOP
        EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON tg.%I FOR EACH ROW
                        EXECUTE FUNCTION tg.forbid_modification()', t || '_no_change', t);
        EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON tg.%I FOR EACH STATEMENT
                        EXECUTE FUNCTION tg.forbid_modification()', t || '_no_truncate', t);
    END LOOP;
    -- chained into the audit log (deliveries and once-markers are operational, not evidence)
    FOREACH t IN ARRAY ARRAY ['receipt_prints', 'snapshot_labels', 'review_samples', 'ticket_photos',
                              'payment_batches', 'payment_items'] LOOP
        EXECUTE format('CREATE TRIGGER %I AFTER INSERT ON tg.%I FOR EACH ROW EXECUTE FUNCTION tg.audit_row()',
                       t || '_audit', t);
    END LOOP;
    EXECUTE 'CREATE TRIGGER channel_secrets_no_delete BEFORE DELETE ON tg.channel_secrets
             FOR EACH ROW EXECUTE FUNCTION tg.forbid_modification()';
    EXECUTE 'CREATE TRIGGER channel_secrets_audit AFTER INSERT OR UPDATE ON tg.channel_secrets
             FOR EACH ROW EXECUTE FUNCTION tg.audit_row(''value_enc'')';
END
$$;

-- ------------------------------------------------------------------ settings

INSERT INTO tg.settings (key, value)
VALUES ('site.timezone', '"Africa/Algiers"'),
       ('gate.min_unload_s', '180'),
       ('gate.entry_decision_alert_s', '600'),
       ('review.sample_percent', '5'),
       ('trip.price_per_trip', '0'),
       ('trip.currency', '"DZD"'),
       ('trip.partial_alert_threshold', '3'),
       ('trip.partial_alert_window_days', '7'),
       ('alerts.language', '"ar"'),
       ('alerts.sms_enabled', 'false'),
       ('alerts.sms_port', '""'),
       ('alerts.sms_recipients', '[]'),
       ('alerts.sms_min_severity', '"CRITICAL"'),
       ('alerts.sms_max_per_hour', '10'),
       ('alerts.telegram_enabled', 'false'),
       ('alerts.telegram_chat_ids', '[]'),
       ('alerts.telegram_min_severity', '"MEDIUM"'),
       ('alerts.telegram_photos', 'true');

-- ------------------------------------------------------------------ grants

DO
$$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'truckgate_app') THEN
        GRANT SELECT, INSERT ON tg.receipt_prints, tg.snapshot_labels, tg.review_samples, tg.alert_deliveries,
            tg.channel_secrets, tg.alert_once, tg.ticket_photos, tg.payment_batches, tg.payment_items
            TO truckgate_app;
        GRANT UPDATE ON tg.channel_secrets TO truckgate_app;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA tg TO truckgate_app;
        GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA tg TO truckgate_app;
    END IF;
END
$$;
