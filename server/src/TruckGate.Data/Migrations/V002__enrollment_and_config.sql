-- =====================================================================================
-- TruckGate V002 — quarry phone enrollment, signed device configuration, operator PINs
-- =====================================================================================

-- Second key per phone: signs background requests (config, sync). The ticket key needs a
-- fingerprint for every signature, so it cannot be used for that.
ALTER TABLE tg.devices ADD COLUMN auth_key_spki bytea UNIQUE;

CREATE OR REPLACE FUNCTION tg.devices_guard() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    IF NEW.id <> OLD.id OR NEW.kind <> OLD.kind OR NEW.public_key_spki <> OLD.public_key_spki
        OR NEW.enrolled_at <> OLD.enrolled_at OR NEW.attestation IS DISTINCT FROM OLD.attestation
        OR NEW.auth_key_spki IS DISTINCT FROM OLD.auth_key_spki THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: device identity cannot change' USING ERRCODE = 'P0001';
    END IF;
    IF OLD.revoked_at IS NOT NULL AND (NEW.revoked_at IS DISTINCT FROM OLD.revoked_at
        OR NEW.revoke_reason IS DISTINCT FROM OLD.revoke_reason) THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: a revoked device stays revoked' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END
$$;

ALTER TABLE tg.users ADD COLUMN pin_set_at timestamptz;

-- One-time enrollment codes shown as a QR on the admin screen. Only the SHA-256 of the
-- token is stored.
CREATE TABLE tg.enrollment_tokens
(
    id           bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    token_sha256 bytea       NOT NULL UNIQUE,
    label        text        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    expires_at   timestamptz NOT NULL,
    used_at      timestamptz,
    device_id    integer REFERENCES tg.devices (id),
    CHECK ((used_at IS NULL) = (device_id IS NULL)),
    CHECK (expires_at > created_at)
);

CREATE FUNCTION tg.enrollment_tokens_guard() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    IF NEW.id <> OLD.id OR NEW.token_sha256 <> OLD.token_sha256 OR NEW.label <> OLD.label
        OR NEW.created_at <> OLD.created_at OR NEW.expires_at <> OLD.expires_at OR OLD.used_at IS NOT NULL THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: an enrollment code is used once' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER enrollment_tokens_guard BEFORE UPDATE ON tg.enrollment_tokens
    FOR EACH ROW EXECUTE FUNCTION tg.enrollment_tokens_guard();

-- Every distinct configuration content gets a version number (the id), which the phone puts
-- in each ticket. The summary never contains PIN hashes.
CREATE TABLE tg.device_configs
(
    id             integer PRIMARY KEY GENERATED ALWAYS AS IDENTITY CHECK (id BETWEEN 1 AND 65535),
    content_sha256 bytea       NOT NULL UNIQUE,
    summary        jsonb       NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now()
);

DO
$$
DECLARE
    t text;
BEGIN
    EXECUTE 'CREATE TRIGGER device_configs_no_change BEFORE UPDATE OR DELETE ON tg.device_configs
             FOR EACH ROW EXECUTE FUNCTION tg.forbid_modification()';
    EXECUTE 'CREATE TRIGGER enrollment_tokens_no_delete BEFORE DELETE ON tg.enrollment_tokens
             FOR EACH ROW EXECUTE FUNCTION tg.forbid_modification()';
    FOREACH t IN ARRAY ARRAY ['enrollment_tokens', 'device_configs'] LOOP
        EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON tg.%I FOR EACH STATEMENT
                        EXECUTE FUNCTION tg.forbid_modification()', t || '_no_truncate', t);
        EXECUTE format('CREATE TRIGGER %I AFTER INSERT OR UPDATE ON tg.%I FOR EACH ROW
                        EXECUTE FUNCTION tg.audit_row(''token_sha256'')', t || '_audit', t);
    END LOOP;
END
$$;

-- Quarry phone parameters (delivered in the signed configuration).
INSERT INTO tg.settings (key, value)
VALUES ('quarry.truck_cooldown_s', '1200'),
       ('quarry.clock_mismatch_s', '300'),
       ('quarry.sync_max_age_s', '86400'),
       ('quarry.gps_max_age_s', '120'),
       ('quarry.gps_max_accuracy_m', '150'),
       ('quarry.max_unsynced_tickets', '500');

DO
$$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'truckgate_app') THEN
        GRANT SELECT, INSERT ON tg.enrollment_tokens, tg.device_configs TO truckgate_app;
        GRANT UPDATE ON tg.enrollment_tokens TO truckgate_app;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA tg TO truckgate_app;
        GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA tg TO truckgate_app;
    END IF;
END
$$;
