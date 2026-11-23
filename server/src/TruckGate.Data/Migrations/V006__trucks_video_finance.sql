-- =====================================================================================
-- TruckGate V006 — workflow 1.1
--   * trucks registered from a quarry phone (provisional) and approved, rejected or merged
--     by a supervisor; owner names; price per truck with its history
--   * blocks of truck ids reserved for each phone (a phone offline can register a truck)
--   * what the gatekeeper saw (plate typed at the gate for a truck not known yet)
--   * event video (rolling buffer: N seconds before and after each scan) per camera
--   * supervisor investigation notes, human trip labels for the future AI dataset
--   * payments per truck, auditable adjustments, phone sync log
-- Same principles as V001: nothing is deleted, evidence is append-only, every change audited.
-- =====================================================================================

-- ------------------------------------------------------------------ trucks: origin and approval

ALTER TABLE tg.trucks
    ADD COLUMN approval_status       text        NOT NULL DEFAULT 'APPROVED'
        CHECK (approval_status IN ('PENDING_APPROVAL', 'APPROVED', 'REJECTED', 'MERGED')),
    ADD COLUMN owner_last_name       text,
    ADD COLUMN owner_first_name      text,
    ADD COLUMN created_via           text        NOT NULL DEFAULT 'ADMIN' CHECK (created_via IN ('ADMIN', 'PHONE')),
    ADD COLUMN created_by_user       integer REFERENCES tg.users (id),
    ADD COLUMN created_by_device     integer REFERENCES tg.devices (id),
    ADD COLUMN reported_plate        text,
    ADD COLUMN possible_duplicate_of integer REFERENCES tg.trucks (id),
    ADD COLUMN decided_by            integer REFERENCES tg.users (id),
    ADD COLUMN decided_at            timestamptz,
    ADD COLUMN decision_reason       text,
    ADD COLUMN merged_into           integer REFERENCES tg.trucks (id),
    ADD COLUMN updated_at            timestamptz NOT NULL DEFAULT now();

-- Trucks that existed before 1.1 were created by an administrator: approved from the start.
UPDATE tg.trucks SET decided_at = created_at, updated_at = created_at;

ALTER TABLE tg.trucks
    ADD CONSTRAINT trucks_decision_check CHECK ((approval_status = 'PENDING_APPROVAL') = (decided_at IS NULL)),
    ADD CONSTRAINT trucks_merge_check CHECK ((approval_status = 'MERGED') = (merged_into IS NOT NULL) AND merged_into IS DISTINCT FROM id),
    ADD CONSTRAINT trucks_reject_check CHECK (approval_status <> 'REJECTED' OR decision_reason IS NOT NULL),
    ADD CONSTRAINT trucks_phone_check CHECK (created_via <> 'PHONE' OR (created_by_device IS NOT NULL AND reported_plate IS NOT NULL));
CREATE INDEX ON tg.trucks (approval_status);

-- A phone-registered truck keeps what the worker typed; a decision is taken once.
CREATE FUNCTION tg.trucks_guard() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    IF NEW.id <> OLD.id OR NEW.created_at <> OLD.created_at OR NEW.created_via <> OLD.created_via
        OR NEW.created_by_user IS DISTINCT FROM OLD.created_by_user
        OR NEW.created_by_device IS DISTINCT FROM OLD.created_by_device
        OR NEW.reported_plate IS DISTINCT FROM OLD.reported_plate THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: truck origin cannot change' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.approval_status <> OLD.approval_status THEN
        IF OLD.approval_status <> 'PENDING_APPROVAL' THEN
            RAISE EXCEPTION 'TG_STATE: truck % is already %', OLD.id, OLD.approval_status USING ERRCODE = 'P0001';
        END IF;
    ELSIF NEW.decided_at IS DISTINCT FROM OLD.decided_at OR NEW.decided_by IS DISTINCT FROM OLD.decided_by
        OR NEW.merged_into IS DISTINCT FROM OLD.merged_into OR NEW.decision_reason IS DISTINCT FROM OLD.decision_reason THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: a truck decision cannot be changed' USING ERRCODE = 'P0001';
    END IF;
    NEW.updated_at := now();
    RETURN NEW;
END
$$;
CREATE TRIGGER trucks_guard BEFORE UPDATE ON tg.trucks FOR EACH ROW EXECUTE FUNCTION tg.trucks_guard();

-- The truck a trip counts for: a provisional truck merged into an existing one counts for that one.
CREATE FUNCTION tg.effective_truck_id(p_truck integer) RETURNS integer
    STABLE LANGUAGE plpgsql AS
$$
DECLARE
    v   integer := p_truck;
    nxt integer;
    i   integer := 0;
BEGIN
    LOOP
        SELECT merged_into INTO nxt FROM tg.trucks WHERE id = v;
        EXIT WHEN nxt IS NULL OR i >= 10;
        v := nxt;
        i := i + 1;
    END LOOP;
    RETURN v;
END
$$;

-- Digits only, Arabic-Indic digits included: "01234-216-16" and "٠١٢٣٤ ٢١٦ ١٦" compare equal.
CREATE FUNCTION tg.plate_key(p text) RETURNS text
    IMMUTABLE LANGUAGE sql AS
$$ SELECT regexp_replace(translate(coalesce(p, ''), '٠١٢٣٤٥٦٧٨٩۰۱۲۳۴۵۶۷۸۹', '01234567890123456789'), '[^0-9A-Za-z]', '', 'g') $$;

-- ------------------------------------------------------------------ price per truck (history)

CREATE TABLE tg.truck_prices
(
    id         bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    truck_id   integer        NOT NULL REFERENCES tg.trucks (id),
    price      numeric(14, 2) NOT NULL CHECK (price > 0),
    valid_from timestamptz    NOT NULL DEFAULT now(),
    set_by     integer REFERENCES tg.users (id),
    reason     text,
    created_at timestamptz    NOT NULL DEFAULT now()
);
CREATE INDEX ON tg.truck_prices (truck_id, valid_from);

-- Price in force when the trip entered; trips older than the first price use the first price.
CREATE FUNCTION tg.truck_price_at(p_truck integer, p_at timestamptz) RETURNS numeric
    STABLE LANGUAGE sql AS
$$
SELECT coalesce(
    (SELECT price FROM tg.truck_prices WHERE truck_id = p_truck AND valid_from <= p_at ORDER BY valid_from DESC, id DESC LIMIT 1),
    (SELECT price FROM tg.truck_prices WHERE truck_id = p_truck ORDER BY valid_from, id LIMIT 1))
$$;

-- Existing trucks keep paying what the global price said until now (no change in statements).
INSERT INTO tg.truck_prices (truck_id, price, reason)
SELECT t.id, (s.value #>> '{}')::numeric, 'MIGRATION_FROM_GLOBAL_PRICE'
FROM tg.trucks t
JOIN tg.settings s ON s.key = 'trip.price_per_trip'
WHERE jsonb_typeof(s.value) = 'number' AND (s.value #>> '{}')::numeric > 0;

-- ------------------------------------------------------------------ truck ids reserved per phone

-- Ids 40000..65535 are given to phones in blocks, inside their signed configuration, so that a
-- phone without network can register a new truck and print a valid ticket for it.
CREATE TABLE tg.truck_id_blocks
(
    id         bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    device_id  integer     NOT NULL REFERENCES tg.devices (id),
    first_id   integer     NOT NULL UNIQUE CHECK (first_id BETWEEN 40000 AND 65535),
    last_id    integer     NOT NULL UNIQUE CHECK (last_id BETWEEN 40000 AND 65535),
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (last_id >= first_id)
);
CREATE INDEX ON tg.truck_id_blocks (device_id);

-- ------------------------------------------------------------------ gate: what the gatekeeper saw

ALTER TABLE tg.gate_decisions ADD COLUMN observed_plate text CHECK (observed_plate IS NULL OR length(observed_plate) BETWEEN 1 AND 32);

-- ------------------------------------------------------------------ cameras: event video

ALTER TABLE tg.cameras
    ADD COLUMN stream_url   text,
    ADD COLUMN record_video boolean NOT NULL DEFAULT false,
    ADD COLUMN location     text;
ALTER TABLE tg.cameras ADD CONSTRAINT cameras_stream_check
    CHECK (NOT record_video OR stream_url IS NOT NULL);

CREATE OR REPLACE FUNCTION tg.cameras_guard() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    IF NEW.id <> OLD.id OR NEW.created_at <> OLD.created_at OR NEW.source <> OLD.source THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: camera identity cannot change' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.config_version < OLD.config_version THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: camera config_version cannot go back' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.role <> OLD.role OR NEW.gate_id IS DISTINCT FROM OLD.gate_id OR NEW.snapshot_url <> OLD.snapshot_url
        OR NEW.profile_token IS DISTINCT FROM OLD.profile_token OR NEW.stream_url IS DISTINCT FROM OLD.stream_url THEN
        NEW.config_version := OLD.config_version + 1;
    END IF;
    RETURN NEW;
END
$$;

ALTER TABLE tg.camera_health
    ADD COLUMN recorder_status     text CHECK (recorder_status IN ('OFF', 'STARTING', 'RECORDING', 'ERROR')),
    ADD COLUMN recorder_error      text,
    ADD COLUMN last_frame_at       timestamptz,
    ADD COLUMN recorder_updated_at timestamptz;

-- One row per (scan, camera). The file is written once and never overwritten; its SHA-256 is
-- checked whenever it is served. Status goes RECORDING -> OK | PARTIAL | FAILED, then possibly
-- PURGED when the retention period removes the file (the row and its fingerprint stay).
CREATE TABLE tg.video_recordings
(
    id                    bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    gate_scan_id          bigint      NOT NULL REFERENCES tg.gate_scans (id),
    camera_id             integer     NOT NULL REFERENCES tg.cameras (id),
    camera_config_version integer     NOT NULL,
    direction             text        NOT NULL CHECK (direction IN ('ENTRY', 'EXIT')),
    scan_at               timestamptz NOT NULL,
    window_start          timestamptz NOT NULL,
    window_end            timestamptz NOT NULL,
    status                text        NOT NULL DEFAULT 'RECORDING'
        CHECK (status IN ('RECORDING', 'OK', 'PARTIAL', 'FAILED', 'PURGED')),
    media_start           timestamptz,
    media_end             timestamptz,
    duration_ms           integer CHECK (duration_ms IS NULL OR duration_ms >= 0),
    segments              integer,
    path                  text,
    metadata_path         text,
    sha256                bytea CHECK (sha256 IS NULL OR length(sha256) = 32),
    size_bytes            bigint,
    error                 text,
    created_at            timestamptz NOT NULL DEFAULT now(),
    finalized_at          timestamptz,
    purged_at             timestamptz,
    UNIQUE (gate_scan_id, camera_id),
    CHECK (window_end > window_start),
    CHECK (status NOT IN ('OK', 'PARTIAL') OR (path IS NOT NULL AND sha256 IS NOT NULL AND finalized_at IS NOT NULL)),
    CHECK (status <> 'FAILED' OR (error IS NOT NULL AND finalized_at IS NOT NULL)),
    CHECK ((status = 'PURGED') = (purged_at IS NOT NULL))
);
CREATE INDEX ON tg.video_recordings (gate_scan_id);
CREATE INDEX ON tg.video_recordings (status) WHERE status IN ('RECORDING', 'OK', 'PARTIAL');
CREATE INDEX ON tg.video_recordings (created_at);

CREATE FUNCTION tg.video_recordings_guard() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    IF NEW.id <> OLD.id OR NEW.gate_scan_id <> OLD.gate_scan_id OR NEW.camera_id <> OLD.camera_id
        OR NEW.camera_config_version <> OLD.camera_config_version OR NEW.direction <> OLD.direction
        OR NEW.scan_at <> OLD.scan_at OR NEW.window_start <> OLD.window_start OR NEW.window_end <> OLD.window_end
        OR NEW.created_at <> OLD.created_at THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: recording identity cannot change' USING ERRCODE = 'P0001';
    END IF;
    IF OLD.status = 'RECORDING' AND NEW.status IN ('OK', 'PARTIAL', 'FAILED') THEN
        RETURN NEW;
    END IF;
    IF OLD.status IN ('OK', 'PARTIAL') AND NEW.status = 'PURGED'
        AND NEW.path IS NOT DISTINCT FROM OLD.path AND NEW.sha256 IS NOT DISTINCT FROM OLD.sha256
        AND NEW.size_bytes IS NOT DISTINCT FROM OLD.size_bytes AND NEW.duration_ms IS NOT DISTINCT FROM OLD.duration_ms
        AND NEW.media_start IS NOT DISTINCT FROM OLD.media_start AND NEW.media_end IS NOT DISTINCT FROM OLD.media_end
        AND NEW.metadata_path IS NOT DISTINCT FROM OLD.metadata_path AND NEW.error IS NOT DISTINCT FROM OLD.error
        AND NEW.finalized_at IS NOT DISTINCT FROM OLD.finalized_at THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'TG_STATE: recording % cannot go from % to %', OLD.id, OLD.status, NEW.status USING ERRCODE = 'P0001';
END
$$;
CREATE TRIGGER video_recordings_guard BEFORE UPDATE ON tg.video_recordings
    FOR EACH ROW EXECUTE FUNCTION tg.video_recordings_guard();

-- ------------------------------------------------------------------ review: investigation, trip labels

CREATE TABLE tg.review_notes
(
    id         bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    trip_id    bigint      NOT NULL REFERENCES tg.trips (id),
    kind       text        NOT NULL CHECK (kind IN ('INVESTIGATE', 'NOTE')),
    note       text        NOT NULL CHECK (length(btrim(note)) BETWEEN 1 AND 2000),
    created_by integer REFERENCES tg.users (id),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ON tg.review_notes (trip_id, id);

-- Human labels of a whole trip (dataset for future vision models). The latest row is the
-- current set; older rows stay as history. Never produced automatically.
CREATE TABLE tg.trip_annotations
(
    id           bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    trip_id      bigint      NOT NULL REFERENCES tg.trips (id),
    labels       text[]      NOT NULL CHECK (labels <@ ARRAY ['NORMAL_TRIP', 'EMPTY_TRUCK', 'LOADED_TRUCK',
        'INCOMPLETE_CARGO', 'WRONG_TRUCK', 'PLATE_UNREADABLE', 'POSSIBLE_MISMATCH', 'CAMERA_FAILURE', 'NEEDS_REVIEW']::text[]),
    note         text CHECK (note IS NULL OR length(note) <= 2000),
    annotated_by integer REFERENCES tg.users (id),
    annotated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ON tg.trip_annotations (trip_id, id DESC);

-- ------------------------------------------------------------------ payments per truck, adjustments

ALTER TABLE tg.payment_batches ALTER COLUMN price_per_trip DROP NOT NULL;
ALTER TABLE tg.payment_batches
    ADD COLUMN pricing           text           NOT NULL DEFAULT 'GLOBAL' CHECK (pricing IN ('GLOBAL', 'PER_TRUCK')),
    ADD COLUMN adjustments_total numeric(16, 2) NOT NULL DEFAULT 0;
ALTER TABLE tg.payment_items
    ADD COLUMN price_per_trip     numeric(14, 2),
    ADD COLUMN effective_truck_id integer;

-- A correction never edits a paid item: it is a new signed amount with a reason, paid (or taken
-- back) in the next payment of that truck.
CREATE TABLE tg.payment_adjustments
(
    id         bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    truck_id   integer        NOT NULL REFERENCES tg.trucks (id),
    trip_id    bigint REFERENCES tg.trips (id),
    amount     numeric(14, 2) NOT NULL CHECK (amount <> 0),
    reason     text           NOT NULL CHECK (length(btrim(reason)) BETWEEN 1 AND 1000),
    created_by integer REFERENCES tg.users (id),
    created_at timestamptz    NOT NULL DEFAULT now(),
    batch_id   bigint REFERENCES tg.payment_batches (id),
    applied_at timestamptz,
    CHECK ((batch_id IS NULL) = (applied_at IS NULL))
);
CREATE INDEX ON tg.payment_adjustments (truck_id) WHERE batch_id IS NULL;

CREATE FUNCTION tg.payment_adjustments_guard() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    IF NEW.id <> OLD.id OR NEW.truck_id <> OLD.truck_id OR NEW.trip_id IS DISTINCT FROM OLD.trip_id
        OR NEW.amount <> OLD.amount OR NEW.reason <> OLD.reason OR NEW.created_by IS DISTINCT FROM OLD.created_by
        OR NEW.created_at <> OLD.created_at OR OLD.batch_id IS NOT NULL THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: an adjustment is only applied once' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER payment_adjustments_guard BEFORE UPDATE ON tg.payment_adjustments
    FOR EACH ROW EXECUTE FUNCTION tg.payment_adjustments_guard();

-- ------------------------------------------------------------------ phone sync log

CREATE TABLE tg.sync_runs
(
    id         bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    device_id  integer     NOT NULL REFERENCES tg.devices (id),
    at         timestamptz NOT NULL DEFAULT now(),
    tickets    integer     NOT NULL,
    accepted   integer     NOT NULL,
    rejected   integer     NOT NULL,
    new_trucks integer     NOT NULL DEFAULT 0,
    errors     text[]      NOT NULL DEFAULT '{}'
);
CREATE INDEX ON tg.sync_runs (device_id, at DESC);
CREATE INDEX ON tg.sync_runs (at) WHERE rejected > 0;

-- ------------------------------------------------------------------ guards and audit for the new tables

DO
$$
DECLARE
    t text;
BEGIN
    -- append-only
    FOREACH t IN ARRAY ARRAY ['truck_prices', 'truck_id_blocks', 'review_notes', 'trip_annotations', 'sync_runs'] LOOP
        EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON tg.%I FOR EACH ROW
                        EXECUTE FUNCTION tg.forbid_modification()', t || '_no_change', t);
    END LOOP;
    -- no delete on the mutable ones
    FOREACH t IN ARRAY ARRAY ['video_recordings', 'payment_adjustments'] LOOP
        EXECUTE format('CREATE TRIGGER %I BEFORE DELETE ON tg.%I FOR EACH ROW
                        EXECUTE FUNCTION tg.forbid_modification()', t || '_no_delete', t);
    END LOOP;
    FOREACH t IN ARRAY ARRAY ['truck_prices', 'truck_id_blocks', 'review_notes', 'trip_annotations', 'sync_runs',
                              'video_recordings', 'payment_adjustments'] LOOP
        EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON tg.%I FOR EACH STATEMENT
                        EXECUTE FUNCTION tg.forbid_modification()', t || '_no_truncate', t);
    END LOOP;
    -- audited (the sync log is itself a log: not copied into the audit chain)
    FOREACH t IN ARRAY ARRAY ['truck_prices', 'truck_id_blocks', 'review_notes', 'trip_annotations',
                              'video_recordings', 'payment_adjustments'] LOOP
        EXECUTE format('CREATE TRIGGER %I AFTER INSERT OR UPDATE ON tg.%I FOR EACH ROW EXECUTE FUNCTION tg.audit_row()',
                       t || '_audit', t);
    END LOOP;
END
$$;

-- ------------------------------------------------------------------ settings

INSERT INTO tg.settings (key, value)
VALUES ('site.factory_name', '""'),
       ('quarry.photo_required', 'true'),
       ('quarry.sync_overdue_policy', '"BLOCK"'),
       ('video.pre_s', '30'),
       ('video.post_s', '30'),
       ('video.segment_s', '2'),
       ('video.retention_days', '30'),
       ('video.keep_reviewed', 'true'),
       ('video.failure_opens_review', 'true'),
       ('storage.min_free_gb', '20'),
       ('pay.require_exit_scan', 'true')
ON CONFLICT (key) DO NOTHING;

-- ------------------------------------------------------------------ application role

DO
$$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'truckgate_app') THEN
        GRANT SELECT, INSERT ON tg.truck_prices, tg.truck_id_blocks, tg.video_recordings, tg.review_notes,
            tg.trip_annotations, tg.payment_adjustments, tg.sync_runs TO truckgate_app;
        GRANT UPDATE ON tg.video_recordings, tg.payment_adjustments TO truckgate_app;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA tg TO truckgate_app;
        GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA tg TO truckgate_app;
    END IF;
END
$$;
