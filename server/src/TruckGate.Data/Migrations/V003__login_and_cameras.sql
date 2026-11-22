-- =====================================================================================
-- TruckGate V003 — web login lockout, gate cameras, scan snapshots
-- =====================================================================================

ALTER TABLE tg.users
    ADD COLUMN failed_logins integer NOT NULL DEFAULT 0,
    ADD COLUMN locked_until  timestamptz,
    ADD COLUMN last_login_at timestamptz;

-- The users audit trigger must keep hiding the secret hash (pin_hash) — unchanged since V001.

-- Cameras chosen by the admin to take pictures when a ticket is scanned ("burned").
CREATE TABLE tg.cameras
(
    id                integer PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    label             text        NOT NULL,
    gate_id           smallint REFERENCES tg.gates (id),
    role              text        NOT NULL DEFAULT 'OVERVIEW' CHECK (role IN ('PLATE', 'CARGO', 'OVERVIEW')),
    capture_on_scan   boolean     NOT NULL DEFAULT false,
    frames            integer     NOT NULL DEFAULT 3 CHECK (frames BETWEEN 1 AND 10),
    frame_interval_ms integer     NOT NULL DEFAULT 300 CHECK (frame_interval_ms BETWEEN 0 AND 2000),
    source            text        NOT NULL CHECK (source IN ('ONVIF', 'URL')),
    onvif_url         text,
    profile_token     text,
    snapshot_url      text        NOT NULL,
    username          text,
    password_enc      bytea,
    manufacturer      text,
    model             text,
    serial            text,
    -- Bumped automatically when what the camera sees may have changed (AI training data).
    config_version    integer     NOT NULL DEFAULT 1,
    active            boolean     NOT NULL DEFAULT true,
    created_at        timestamptz NOT NULL DEFAULT now(),
    CHECK (source <> 'ONVIF' OR (onvif_url IS NOT NULL AND profile_token IS NOT NULL))
);

CREATE FUNCTION tg.cameras_guard() RETURNS trigger
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
        OR NEW.profile_token IS DISTINCT FROM OLD.profile_token THEN
        NEW.config_version := OLD.config_version + 1;
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER cameras_guard BEFORE UPDATE ON tg.cameras FOR EACH ROW EXECUTE FUNCTION tg.cameras_guard();

-- Health is operational, not evidence: updated at every capture, not audited.
CREATE TABLE tg.camera_health
(
    camera_id     integer PRIMARY KEY REFERENCES tg.cameras (id),
    last_ok_at    timestamptz,
    last_error    text,
    last_error_at timestamptz
);

-- One row per stored picture. The SHA-256 is chained into the audit log, so a replaced
-- picture file is detected when it is served.
CREATE TABLE tg.snapshots
(
    id                    bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    gate_scan_id          bigint      NOT NULL REFERENCES tg.gate_scans (id),
    camera_id             integer     NOT NULL REFERENCES tg.cameras (id),
    camera_role           text        NOT NULL,
    camera_config_version integer     NOT NULL,
    frame_index           smallint    NOT NULL,
    captured_at           timestamptz NOT NULL,
    path                  text        NOT NULL,
    sha256                bytea       NOT NULL CHECK (length(sha256) = 32),
    size_bytes            integer     NOT NULL,
    UNIQUE (gate_scan_id, camera_id, frame_index)
);
CREATE INDEX ON tg.snapshots (gate_scan_id);

DO
$$
BEGIN
    EXECUTE 'CREATE TRIGGER snapshots_no_change BEFORE UPDATE OR DELETE ON tg.snapshots
             FOR EACH ROW EXECUTE FUNCTION tg.forbid_modification()';
    EXECUTE 'CREATE TRIGGER cameras_no_delete BEFORE DELETE ON tg.cameras
             FOR EACH ROW EXECUTE FUNCTION tg.forbid_modification()';
    EXECUTE 'CREATE TRIGGER camera_health_no_delete BEFORE DELETE ON tg.camera_health
             FOR EACH ROW EXECUTE FUNCTION tg.forbid_modification()';
    EXECUTE 'CREATE TRIGGER snapshots_no_truncate BEFORE TRUNCATE ON tg.snapshots
             FOR EACH STATEMENT EXECUTE FUNCTION tg.forbid_modification()';
    EXECUTE 'CREATE TRIGGER cameras_no_truncate BEFORE TRUNCATE ON tg.cameras
             FOR EACH STATEMENT EXECUTE FUNCTION tg.forbid_modification()';
    EXECUTE 'CREATE TRIGGER snapshots_audit AFTER INSERT ON tg.snapshots
             FOR EACH ROW EXECUTE FUNCTION tg.audit_row()';
    -- the camera password (even encrypted) never goes to the audit log
    EXECUTE 'CREATE TRIGGER cameras_audit AFTER INSERT OR UPDATE ON tg.cameras
             FOR EACH ROW EXECUTE FUNCTION tg.audit_row(''password_enc'')';
END
$$;

INSERT INTO tg.settings (key, value)
VALUES ('auth.max_failed_logins', '5'),
       ('auth.lockout_s', '300');

DO
$$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'truckgate_app') THEN
        GRANT SELECT, INSERT ON tg.cameras, tg.camera_health, tg.snapshots TO truckgate_app;
        GRANT UPDATE ON tg.cameras, tg.camera_health TO truckgate_app;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA tg TO truckgate_app;
        GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA tg TO truckgate_app;
    END IF;
END
$$;
