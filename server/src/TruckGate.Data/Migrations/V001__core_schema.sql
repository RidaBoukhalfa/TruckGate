-- =====================================================================================
-- TruckGate V001 — core schema
--
-- Principles (see docs/DECISIONS.md):
--   * Nothing is ever deleted. DELETE and TRUNCATE are refused on every table.
--   * Evidence tables are append-only (no UPDATE either).
--   * Mutable tables only allow the column changes listed in their guard trigger.
--   * Every row change is written to tg.audit_log, which is hash-chained inside the
--     database: the chain cannot be bypassed by the application, and any later edit
--     made by a superuser who disables triggers is detected by tg.audit_verify().
--   * Run under READ COMMITTED (PostgreSQL default). The audit chain relies on it.
-- =====================================================================================

CREATE SCHEMA tg;
REVOKE ALL ON SCHEMA tg FROM PUBLIC;

-- ------------------------------------------------------------------ helpers

CREATE FUNCTION tg.fmt_seq(n bigint) RETURNS text
    IMMUTABLE LANGUAGE sql AS
$$ SELECT CASE WHEN n >= 1000000 THEN n::text ELSE lpad(n::text, 6, '0') END $$;

-- Who is acting. The application sets tg.actor per transaction (e.g. 'user:12').
-- Anything done outside the application shows up as 'db:<role>'.
CREATE FUNCTION tg.current_actor() RETURNS text
    STABLE LANGUAGE sql AS
$$ SELECT coalesce(nullif(current_setting('tg.actor', true), ''), 'db:' || session_user::text) $$;

CREATE FUNCTION tg.forbid_modification() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    RAISE EXCEPTION 'TG_APPEND_ONLY: % on %.% is not allowed', TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME
        USING ERRCODE = 'P0001';
END
$$;

-- ------------------------------------------------------------------ audit chain

-- Length-prefixed field so that no separator character can be abused.
CREATE FUNCTION tg.lp(v text) RETURNS text
    IMMUTABLE LANGUAGE sql AS
$$ SELECT CASE WHEN v IS NULL THEN '-1:' ELSE length(v)::text || ':' || v END $$;

CREATE FUNCTION tg.audit_digest(p_id bigint, p_at timestamptz, p_actor text, p_action text, p_entity text,
                                p_entity_id text, p_payload jsonb, p_prev bytea) RETURNS bytea
    IMMUTABLE LANGUAGE sql AS
$$
SELECT sha256(p_prev || convert_to(
        tg.lp(p_id::text) ||
        tg.lp(((extract(epoch FROM p_at) * 1000000)::bigint)::text) ||
        tg.lp(p_actor) || tg.lp(p_action) || tg.lp(p_entity) || tg.lp(p_entity_id) ||
        tg.lp(p_payload::text), 'UTF8'))
$$;

CREATE SEQUENCE tg.audit_log_id_seq;

CREATE TABLE tg.audit_log
(
    id        bigint PRIMARY KEY,
    at        timestamptz NOT NULL,
    actor     text        NOT NULL,
    action    text        NOT NULL,
    entity    text        NOT NULL,
    entity_id text,
    payload   jsonb       NOT NULL DEFAULT '{}'::jsonb,
    prev_hash bytea       NOT NULL,
    hash      bytea       NOT NULL
);

-- Serializes writers, assigns id/time/actor and computes the hash. Callers only supply
-- action, entity, entity_id and payload; anything else they pass is overwritten.
CREATE FUNCTION tg.audit_chain() RETURNS trigger
    LANGUAGE plpgsql AS
$$
DECLARE
    v_prev bytea;
BEGIN
    PERFORM pg_advisory_xact_lock(7340001);
    SELECT a.hash INTO v_prev FROM tg.audit_log a ORDER BY a.id DESC LIMIT 1;
    NEW.id := nextval('tg.audit_log_id_seq');
    NEW.at := clock_timestamp();
    NEW.actor := tg.current_actor();
    NEW.payload := coalesce(NEW.payload, '{}'::jsonb);
    NEW.prev_hash := coalesce(v_prev, decode(repeat('00', 32), 'hex'));
    NEW.hash := tg.audit_digest(NEW.id, NEW.at, NEW.actor, NEW.action, NEW.entity, NEW.entity_id,
                                NEW.payload, NEW.prev_hash);
    RETURN NEW;
END
$$;

CREATE TRIGGER audit_chain BEFORE INSERT ON tg.audit_log FOR EACH ROW EXECUTE FUNCTION tg.audit_chain();
CREATE TRIGGER audit_log_no_change BEFORE UPDATE OR DELETE ON tg.audit_log
    FOR EACH ROW EXECUTE FUNCTION tg.forbid_modification();
CREATE TRIGGER audit_log_no_truncate BEFORE TRUNCATE ON tg.audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION tg.forbid_modification();

-- Anchors: periodic copies of the latest hash, also sent outside the factory (SMS/Telegram)
-- so that even a rewrite of the whole chain by a superuser is detectable.
CREATE TABLE tg.audit_anchors
(
    id         bigserial PRIMARY KEY,
    audit_id   bigint      NOT NULL,
    hash       bytea       NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TRIGGER audit_anchors_no_change BEFORE UPDATE OR DELETE ON tg.audit_anchors
    FOR EACH ROW EXECUTE FUNCTION tg.forbid_modification();
CREATE TRIGGER audit_anchors_no_truncate BEFORE TRUNCATE ON tg.audit_anchors
    FOR EACH STATEMENT EXECUTE FUNCTION tg.forbid_modification();

CREATE FUNCTION tg.audit_anchor() RETURNS TABLE (audit_id bigint, hash bytea, created_at timestamptz)
    LANGUAGE sql AS
$$
INSERT INTO tg.audit_anchors (audit_id, hash)
SELECT a.id, a.hash FROM tg.audit_log a ORDER BY a.id DESC LIMIT 1
RETURNING audit_id, hash, created_at
$$;

CREATE FUNCTION tg.audit_verify()
    RETURNS TABLE (checked bigint, first_bad_id bigint, problem text)
    STABLE LANGUAGE plpgsql AS
$$
DECLARE
    r      record;
    v_prev bytea  := decode(repeat('00', 32), 'hex');
    n      bigint := 0;
BEGIN
    FOR r IN SELECT * FROM tg.audit_log ORDER BY id LOOP
        n := n + 1;
        IF r.prev_hash <> v_prev THEN
            checked := n; first_bad_id := r.id; problem := 'PREV_HASH_MISMATCH'; RETURN NEXT; RETURN;
        END IF;
        IF r.hash <> tg.audit_digest(r.id, r.at, r.actor, r.action, r.entity, r.entity_id, r.payload, r.prev_hash) THEN
            checked := n; first_bad_id := r.id; problem := 'HASH_MISMATCH'; RETURN NEXT; RETURN;
        END IF;
        v_prev := r.hash;
    END LOOP;
    FOR r IN SELECT an.audit_id, an.hash, a.hash AS actual
             FROM tg.audit_anchors an LEFT JOIN tg.audit_log a ON a.id = an.audit_id
             ORDER BY an.id LOOP
        IF r.actual IS NULL THEN
            checked := n; first_bad_id := r.audit_id; problem := 'ANCHOR_MISSING'; RETURN NEXT; RETURN;
        ELSIF r.actual <> r.hash THEN
            checked := n; first_bad_id := r.audit_id; problem := 'ANCHOR_MISMATCH'; RETURN NEXT; RETURN;
        END IF;
    END LOOP;
    checked := n; first_bad_id := NULL; problem := NULL; RETURN NEXT;
END
$$;

-- Generic row audit. Trigger arguments = column names to leave out of the payload (secrets).
CREATE FUNCTION tg.audit_row() RETURNS trigger
    LANGUAGE plpgsql AS
$$
DECLARE
    v_new jsonb;
    v_old jsonb;
    v_ref jsonb;
BEGIN
    IF TG_OP IN ('INSERT', 'UPDATE') THEN v_new := to_jsonb(NEW); END IF;
    IF TG_OP IN ('UPDATE', 'DELETE') THEN v_old := to_jsonb(OLD); END IF;
    IF TG_NARGS > 0 THEN
        v_new := v_new - TG_ARGV::text[];
        v_old := v_old - TG_ARGV::text[];
    END IF;
    IF TG_OP = 'UPDATE' AND v_new = v_old THEN
        RETURN NULL;
    END IF;
    v_ref := coalesce(v_new, v_old);
    INSERT INTO tg.audit_log (action, entity, entity_id, payload)
    VALUES (lower(TG_OP), TG_TABLE_NAME,
            coalesce(v_ref ->> 'id', v_ref ->> 'ticket_id', v_ref ->> 'key'),
            jsonb_strip_nulls(jsonb_build_object('old', v_old, 'new', v_new)));
    RETURN NULL;
END
$$;

-- ------------------------------------------------------------------ reference data (mutable, audited)

CREATE TABLE tg.settings
(
    key        text PRIMARY KEY,
    value      jsonb       NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tg.sites
(
    id       smallint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    code     text NOT NULL UNIQUE,
    name     text NOT NULL,
    kind     text NOT NULL CHECK (kind IN ('QUARRY', 'FACTORY')),
    geofence jsonb
);

CREATE TABLE tg.users
(
    id         integer PRIMARY KEY GENERATED ALWAYS AS IDENTITY CHECK (id BETWEEN 1 AND 65535),
    username   text        NOT NULL UNIQUE,
    full_name  text        NOT NULL,
    role       text        NOT NULL CHECK (role IN ('QUARRY_OPERATOR', 'GATEKEEPER', 'SUPERVISOR',
                                                    'ACCOUNTANT', 'MANAGER', 'ADMIN', 'AUDITOR')),
    pin_hash   text,
    language   text        NOT NULL DEFAULT 'ar' CHECK (language IN ('ar', 'fr')),
    active     boolean     NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tg.drivers
(
    id              integer PRIMARY KEY GENERATED ALWAYS AS IDENTITY CHECK (id BETWEEN 1 AND 65535),
    full_name       text        NOT NULL,
    phone           text,
    employment_type text        NOT NULL DEFAULT 'EMPLOYEE' CHECK (employment_type IN ('EMPLOYEE', 'CONTRACTOR')),
    active          boolean     NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now()
);

-- truck id is the u16 carried in tickets, so it is assigned explicitly.
CREATE TABLE tg.trucks
(
    id                integer PRIMARY KEY CHECK (id BETWEEN 1 AND 65535),
    fleet_number      text        NOT NULL UNIQUE,
    model             text,
    photo_path        text,
    default_driver_id integer REFERENCES tg.drivers (id),
    active            boolean     NOT NULL DEFAULT true,
    created_at        timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tg.truck_plates
(
    id         bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    truck_id   integer     NOT NULL REFERENCES tg.trucks (id),
    plate_text text        NOT NULL,
    valid_from timestamptz NOT NULL DEFAULT now(),
    valid_to   timestamptz,
    CHECK (valid_to IS NULL OR valid_to > valid_from)
);
CREATE INDEX ON tg.truck_plates (truck_id);

CREATE TABLE tg.gates
(
    id      smallint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    code    text     NOT NULL UNIQUE,
    name    text     NOT NULL,
    site_id smallint REFERENCES tg.sites (id)
);

-- device id is the u16 carried in tickets, so it is assigned explicitly.
CREATE TABLE tg.devices
(
    id              integer PRIMARY KEY CHECK (id BETWEEN 1 AND 65535),
    kind            text        NOT NULL CHECK (kind IN ('QUARRY_PHONE', 'GATE_STATION')),
    label           text        NOT NULL,
    public_key_spki bytea       NOT NULL UNIQUE,
    attestation     bytea,
    enrolled_at     timestamptz NOT NULL DEFAULT now(),
    revoked_at      timestamptz,
    revoke_reason   text,
    last_sync_at    timestamptz,
    CHECK ((revoked_at IS NULL) = (revoke_reason IS NULL))
);

CREATE FUNCTION tg.devices_guard() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    IF NEW.id <> OLD.id OR NEW.kind <> OLD.kind OR NEW.public_key_spki <> OLD.public_key_spki
        OR NEW.enrolled_at <> OLD.enrolled_at OR NEW.attestation IS DISTINCT FROM OLD.attestation THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: device identity cannot change' USING ERRCODE = 'P0001';
    END IF;
    IF OLD.revoked_at IS NOT NULL AND (NEW.revoked_at IS DISTINCT FROM OLD.revoked_at
        OR NEW.revoke_reason IS DISTINCT FROM OLD.revoke_reason) THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: a revoked device stays revoked' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER devices_guard BEFORE UPDATE ON tg.devices FOR EACH ROW EXECUTE FUNCTION tg.devices_guard();

-- ------------------------------------------------------------------ tickets and gate (append-only evidence)

CREATE TABLE tg.tickets
(
    device_id      integer     NOT NULL REFERENCES tg.devices (id),
    seq            bigint      NOT NULL CHECK (seq BETWEEN 0 AND 4294967295),
    ticket_id      text GENERATED ALWAYS AS ('Q' || device_id::text || '-' || tg.fmt_seq(seq)) STORED,
    kind           smallint    NOT NULL,
    issued_at      timestamptz NOT NULL,
    clock_delta    smallint    NOT NULL,
    truck_id       integer     NOT NULL,
    driver_id      integer     NOT NULL,
    operator_id    integer     NOT NULL,
    lat_e6         integer     NOT NULL,
    lon_e6         integer     NOT NULL,
    flags          smallint    NOT NULL,
    config_version integer     NOT NULL,
    photo_sha256   bytea       NOT NULL CHECK (length(photo_sha256) = 32),
    raw            bytea       NOT NULL CHECK (length(raw) = 127),
    first_seen_at  timestamptz NOT NULL DEFAULT now(),
    first_seen_via text        NOT NULL CHECK (first_seen_via IN ('GATE', 'SYNC', 'MANUAL')),
    PRIMARY KEY (device_id, seq),
    UNIQUE (ticket_id)
);
CREATE INDEX ON tg.tickets (truck_id, issued_at);

CREATE TABLE tg.ticket_events
(
    id        bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    device_id integer     NOT NULL,
    seq       bigint      NOT NULL,
    type      text        NOT NULL CHECK (type IN ('ISSUED', 'PRINTED', 'REPRINTED', 'VOIDED', 'SYNCED')),
    at        timestamptz NOT NULL DEFAULT now(),
    reason    text,
    payload   jsonb,
    FOREIGN KEY (device_id, seq) REFERENCES tg.tickets (device_id, seq),
    CHECK (type <> 'VOIDED' OR reason IS NOT NULL)
);
CREATE INDEX ON tg.ticket_events (device_id, seq);

CREATE TABLE tg.gate_scans
(
    id               bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    gate_id          smallint    NOT NULL REFERENCES tg.gates (id),
    direction        text        NOT NULL CHECK (direction IN ('ENTRY', 'EXIT')),
    scanned_at       timestamptz NOT NULL DEFAULT now(),
    scan_type        text        NOT NULL CHECK (scan_type IN ('QUARRY_TICKET', 'GATE_RECEIPT', 'MANUAL_CODE')),
    raw_text         text        NOT NULL,
    ticket_device_id integer,
    ticket_seq       bigint,
    outcome          text        NOT NULL CHECK (outcome IN ('ACCEPT', 'PENDING', 'REJECT')),
    reasons          text[]      NOT NULL DEFAULT '{}',
    notes            text[]      NOT NULL DEFAULT '{}',
    gatekeeper_id    integer REFERENCES tg.users (id),
    FOREIGN KEY (ticket_device_id, ticket_seq) REFERENCES tg.tickets (device_id, seq)
);
CREATE INDEX ON tg.gate_scans (scanned_at);

CREATE TABLE tg.gate_decisions
(
    id           bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    gate_scan_id bigint      NOT NULL UNIQUE REFERENCES tg.gate_scans (id),
    decision     text        NOT NULL CHECK (decision IN ('FULL', 'PARTIAL', 'EMPTY', 'DIFFERENT_TRUCK',
                                                          'PLATE_UNREADABLE', 'EXIT_EMPTY', 'EXIT_LOADED')),
    decided_by   integer REFERENCES tg.users (id),
    decided_at   timestamptz NOT NULL DEFAULT now(),
    note         text
);

-- ------------------------------------------------------------------ trips (state machine)

CREATE TABLE tg.trip_transitions
(
    from_state text NOT NULL,
    to_state   text NOT NULL,
    PRIMARY KEY (from_state, to_state)
);
INSERT INTO tg.trip_transitions (from_state, to_state)
VALUES ('NEW', 'AT_GATE'),
       ('AT_GATE', 'ENTERED'),
       ('AT_GATE', 'PENDING_REVIEW'),
       ('ENTERED', 'PENDING_REVIEW'),
       ('ENTERED', 'EXITED_EMPTY'),
       ('PENDING_REVIEW', 'ENTERED'),
       ('PENDING_REVIEW', 'EXITED_EMPTY'),
       ('PENDING_REVIEW', 'REJECTED'),
       ('EXITED_EMPTY', 'PENDING_REVIEW'),
       ('EXITED_EMPTY', 'CLOSED'),
       ('CLOSED', 'PAID');

CREATE TABLE tg.trips
(
    id               bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    trip_number      text GENERATED ALWAYS AS ('T-' || tg.fmt_seq(id)) STORED UNIQUE,
    ticket_device_id integer     NOT NULL,
    ticket_seq       bigint      NOT NULL,
    truck_id         integer     NOT NULL,
    driver_id        integer     NOT NULL,
    entry_scan_id    bigint      NOT NULL UNIQUE REFERENCES tg.gate_scans (id),
    exit_scan_id     bigint UNIQUE REFERENCES tg.gate_scans (id),
    load_status      text CHECK (load_status IN ('FULL', 'PARTIAL', 'EMPTY')),
    current_state    text        NOT NULL DEFAULT 'AT_GATE',
    state_changed_at timestamptz NOT NULL DEFAULT now(),
    created_at       timestamptz NOT NULL DEFAULT now(),
    -- one trip per ticket, for ever: the database itself refuses a second use
    UNIQUE (ticket_device_id, ticket_seq),
    FOREIGN KEY (ticket_device_id, ticket_seq) REFERENCES tg.tickets (device_id, seq)
);
CREATE INDEX ON tg.trips (truck_id, created_at);
CREATE INDEX ON tg.trips (current_state);

CREATE TABLE tg.trip_state_events
(
    id          bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    trip_id     bigint      NOT NULL REFERENCES tg.trips (id),
    from_state  text,
    to_state    text        NOT NULL,
    reason_code text        NOT NULL,
    actor       text        NOT NULL DEFAULT '',
    at          timestamptz NOT NULL DEFAULT now(),
    note        text
);
CREATE INDEX ON tg.trip_state_events (trip_id, id);

CREATE FUNCTION tg.trip_event_check() RETURNS trigger
    LANGUAGE plpgsql AS
$$
DECLARE
    v_cur text;
    v_has boolean;
BEGIN
    SELECT current_state INTO v_cur FROM tg.trips WHERE id = NEW.trip_id FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'TG_STATE: trip % does not exist', NEW.trip_id USING ERRCODE = 'P0001';
    END IF;
    v_has := EXISTS (SELECT 1 FROM tg.trip_state_events e WHERE e.trip_id = NEW.trip_id);
    IF NOT v_has THEN
        IF NEW.from_state IS NOT NULL OR NEW.to_state <> v_cur THEN
            RAISE EXCEPTION 'TG_STATE: first event must be NULL -> %', v_cur USING ERRCODE = 'P0001';
        END IF;
    ELSIF NEW.from_state IS DISTINCT FROM v_cur THEN
        RAISE EXCEPTION 'TG_STATE: trip % is %, not %', NEW.trip_id, v_cur, NEW.from_state USING ERRCODE = 'P0001';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM tg.trip_transitions t
                   WHERE t.from_state = coalesce(NEW.from_state, 'NEW') AND t.to_state = NEW.to_state) THEN
        RAISE EXCEPTION 'TG_STATE: transition % -> % is not allowed', coalesce(NEW.from_state, 'NEW'), NEW.to_state
            USING ERRCODE = 'P0001';
    END IF;
    NEW.actor := tg.current_actor();
    NEW.at := now();
    RETURN NEW;
END
$$;
CREATE TRIGGER trip_event_check BEFORE INSERT ON tg.trip_state_events
    FOR EACH ROW EXECUTE FUNCTION tg.trip_event_check();

CREATE FUNCTION tg.trip_event_apply() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    UPDATE tg.trips SET current_state = NEW.to_state, state_changed_at = NEW.at WHERE id = NEW.trip_id;
    RETURN NULL;
END
$$;
CREATE TRIGGER trip_event_apply AFTER INSERT ON tg.trip_state_events
    FOR EACH ROW EXECUTE FUNCTION tg.trip_event_apply();

CREATE FUNCTION tg.trips_guard() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    IF NEW.id <> OLD.id OR NEW.ticket_device_id <> OLD.ticket_device_id OR NEW.ticket_seq <> OLD.ticket_seq
        OR NEW.truck_id <> OLD.truck_id OR NEW.driver_id <> OLD.driver_id
        OR NEW.entry_scan_id <> OLD.entry_scan_id OR NEW.created_at <> OLD.created_at THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: trip identity cannot change' USING ERRCODE = 'P0001';
    END IF;
    IF OLD.exit_scan_id IS NOT NULL AND NEW.exit_scan_id IS DISTINCT FROM OLD.exit_scan_id THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: exit scan is already recorded' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.current_state IS DISTINCT FROM OLD.current_state
        AND NEW.current_state IS DISTINCT FROM (SELECT e.to_state FROM tg.trip_state_events e
                                                WHERE e.trip_id = NEW.id ORDER BY e.id DESC LIMIT 1) THEN
        RAISE EXCEPTION 'TG_STATE: change trip state by inserting into trip_state_events' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER trips_guard BEFORE UPDATE ON tg.trips FOR EACH ROW EXECUTE FUNCTION tg.trips_guard();

CREATE FUNCTION tg.trips_require_initial_event() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM tg.trip_state_events e WHERE e.trip_id = NEW.id AND e.from_state IS NULL) THEN
        RAISE EXCEPTION 'TG_STATE: trip % has no initial state event', NEW.id USING ERRCODE = 'P0001';
    END IF;
    RETURN NULL;
END
$$;
CREATE CONSTRAINT TRIGGER trips_require_initial_event AFTER INSERT ON tg.trips
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION tg.trips_require_initial_event();

-- ------------------------------------------------------------------ reviews and alerts

CREATE TABLE tg.reviews
(
    id               bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    trip_id          bigint      NOT NULL REFERENCES tg.trips (id),
    reason_codes     text[]      NOT NULL,
    priority         text        NOT NULL CHECK (priority IN ('HIGH', 'NORMAL', 'LOW')),
    is_random_sample boolean     NOT NULL DEFAULT false,
    created_at       timestamptz NOT NULL DEFAULT now(),
    decision         text CHECK (decision IN ('ACCEPT', 'ACCEPT_PARTIAL', 'REJECT')),
    decision_reason  text,
    decided_by       integer REFERENCES tg.users (id),
    decided_at       timestamptz,
    CHECK ((decision IS NULL) = (decided_at IS NULL)),
    CHECK (decision IS DISTINCT FROM 'REJECT' OR decision_reason IS NOT NULL)
);
CREATE INDEX ON tg.reviews (trip_id);
CREATE INDEX ON tg.reviews (created_at) WHERE decision IS NULL;

CREATE FUNCTION tg.reviews_guard() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    IF NEW.id <> OLD.id OR NEW.trip_id <> OLD.trip_id OR NEW.reason_codes <> OLD.reason_codes
        OR NEW.priority <> OLD.priority OR NEW.is_random_sample <> OLD.is_random_sample
        OR NEW.created_at <> OLD.created_at OR OLD.decision IS NOT NULL THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: a review can only be decided once' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER reviews_guard BEFORE UPDATE ON tg.reviews FOR EACH ROW EXECUTE FUNCTION tg.reviews_guard();

CREATE TABLE tg.alerts
(
    id         bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    type       text        NOT NULL,
    severity   text        NOT NULL CHECK (severity IN ('CRITICAL', 'MEDIUM', 'INFO')),
    entity     text,
    entity_id  text,
    params     jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    sms_sent_at      timestamptz,
    telegram_sent_at timestamptz,
    ack_by     integer REFERENCES tg.users (id),
    ack_at     timestamptz
);
CREATE INDEX ON tg.alerts (created_at) WHERE ack_at IS NULL;

CREATE FUNCTION tg.alerts_guard() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    IF NEW.id <> OLD.id OR NEW.type <> OLD.type OR NEW.severity <> OLD.severity
        OR NEW.entity IS DISTINCT FROM OLD.entity OR NEW.entity_id IS DISTINCT FROM OLD.entity_id
        OR NEW.params <> OLD.params OR NEW.created_at <> OLD.created_at
        OR (OLD.sms_sent_at IS NOT NULL AND NEW.sms_sent_at IS DISTINCT FROM OLD.sms_sent_at)
        OR (OLD.telegram_sent_at IS NOT NULL AND NEW.telegram_sent_at IS DISTINCT FROM OLD.telegram_sent_at)
        OR (OLD.ack_at IS NOT NULL AND (NEW.ack_at IS DISTINCT FROM OLD.ack_at OR NEW.ack_by IS DISTINCT FROM OLD.ack_by)) THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: alert content cannot change' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER alerts_guard BEFORE UPDATE ON tg.alerts FOR EACH ROW EXECUTE FUNCTION tg.alerts_guard();

-- ------------------------------------------------------------------ attach guards and audit everywhere

DO
$$
DECLARE
    t text;
BEGIN
    -- evidence: no UPDATE, no DELETE
    FOREACH t IN ARRAY ARRAY ['tickets', 'ticket_events', 'gate_scans', 'gate_decisions',
                              'trip_state_events', 'trip_transitions'] LOOP
        EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON tg.%I FOR EACH ROW
                        EXECUTE FUNCTION tg.forbid_modification()', t || '_no_change', t);
    END LOOP;
    -- every table: no DELETE, no TRUNCATE
    FOREACH t IN ARRAY ARRAY ['settings', 'sites', 'users', 'drivers', 'trucks', 'truck_plates', 'gates',
                              'devices', 'tickets', 'ticket_events', 'gate_scans', 'gate_decisions',
                              'trip_transitions', 'trips', 'trip_state_events', 'reviews', 'alerts'] LOOP
        IF t NOT IN ('tickets', 'ticket_events', 'gate_scans', 'gate_decisions', 'trip_state_events',
                     'trip_transitions') THEN
            EXECUTE format('CREATE TRIGGER %I BEFORE DELETE ON tg.%I FOR EACH ROW
                            EXECUTE FUNCTION tg.forbid_modification()', t || '_no_delete', t);
        END IF;
        EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON tg.%I FOR EACH STATEMENT
                        EXECUTE FUNCTION tg.forbid_modification()', t || '_no_truncate', t);
        -- users: never copy the PIN hash into the audit log
        IF t = 'users' THEN
            EXECUTE format('CREATE TRIGGER %I AFTER INSERT OR UPDATE ON tg.%I FOR EACH ROW
                            EXECUTE FUNCTION tg.audit_row(''pin_hash'')', t || '_audit', t);
        ELSIF t <> 'trip_transitions' THEN
            EXECUTE format('CREATE TRIGGER %I AFTER INSERT OR UPDATE ON tg.%I FOR EACH ROW
                            EXECUTE FUNCTION tg.audit_row()', t || '_audit', t);
        END IF;
    END LOOP;
END
$$;

-- ------------------------------------------------------------------ reports

-- Missing sequence numbers per quarry phone, from tickets seen so far (scans and sync).
CREATE VIEW tg.ticket_sequence_gaps AS
WITH s AS (SELECT device_id, seq, lead(seq) OVER (PARTITION BY device_id ORDER BY seq) AS next_seq,
                  row_number() OVER (PARTITION BY device_id ORDER BY seq)            AS rn
           FROM tg.tickets)
SELECT device_id, 1::bigint AS gap_from, seq - 1 AS gap_to FROM s WHERE rn = 1 AND seq > 1
UNION ALL
SELECT device_id, seq + 1, next_seq - 1 FROM s WHERE next_seq > seq + 1;

-- ------------------------------------------------------------------ defaults

INSERT INTO tg.settings (key, value)
VALUES ('gate.min_travel_s', '600'),
       ('gate.max_validity_s', '5400'),
       ('gate.min_truck_gap_s', '1200'),
       ('gate.future_tolerance_s', '120'),
       ('trip.partial_pay_percent', '100'),
       ('trip.exit_deadline_s', '7200');

-- ------------------------------------------------------------------ application role

-- The role is created once by deploy/db-roles.sql (needs CREATEROLE). If it exists, grant
-- the minimum: read + insert everywhere, update only on mutable tables, never delete.
DO
$$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'truckgate_app') THEN
        GRANT USAGE ON SCHEMA tg TO truckgate_app;
        GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA tg TO truckgate_app;
        GRANT UPDATE ON tg.settings, tg.sites, tg.users, tg.drivers, tg.trucks, tg.truck_plates, tg.gates,
            tg.devices, tg.trips, tg.reviews, tg.alerts TO truckgate_app;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA tg TO truckgate_app;
        GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA tg TO truckgate_app;
        REVOKE INSERT ON tg.trip_transitions FROM truckgate_app;
    END IF;
END
$$;
