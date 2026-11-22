-- V005: WhatsApp alert channel (official Cloud API) and SMS through a 4G/5G router's web interface.

ALTER TABLE tg.alerts ADD COLUMN whatsapp_sent_at timestamptz;

CREATE OR REPLACE FUNCTION tg.alerts_guard() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    IF NEW.id <> OLD.id OR NEW.type <> OLD.type OR NEW.severity <> OLD.severity
        OR NEW.entity IS DISTINCT FROM OLD.entity OR NEW.entity_id IS DISTINCT FROM OLD.entity_id
        OR NEW.params <> OLD.params OR NEW.created_at <> OLD.created_at
        OR (OLD.sms_sent_at IS NOT NULL AND NEW.sms_sent_at IS DISTINCT FROM OLD.sms_sent_at)
        OR (OLD.telegram_sent_at IS NOT NULL AND NEW.telegram_sent_at IS DISTINCT FROM OLD.telegram_sent_at)
        OR (OLD.whatsapp_sent_at IS NOT NULL AND NEW.whatsapp_sent_at IS DISTINCT FROM OLD.whatsapp_sent_at)
        OR (OLD.ack_at IS NOT NULL AND (NEW.ack_at IS DISTINCT FROM OLD.ack_at OR NEW.ack_by IS DISTINCT FROM OLD.ack_by)) THEN
        RAISE EXCEPTION 'TG_IMMUTABLE: alert content cannot change' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END
$$;

ALTER TABLE tg.alert_deliveries DROP CONSTRAINT alert_deliveries_channel_check;
ALTER TABLE tg.alert_deliveries ADD CONSTRAINT alert_deliveries_channel_check
    CHECK (channel IN ('SMS', 'TELEGRAM', 'WHATSAPP'));

INSERT INTO tg.settings (key, value)
VALUES ('alerts.sms_transport', '"MODEM"'),
       ('alerts.sms_router_url', '"http://192.168.0.1"'),
       ('alerts.sms_router_user', '""'),
       ('alerts.whatsapp_enabled', 'false'),
       ('alerts.whatsapp_phone_number_id', '""'),
       ('alerts.whatsapp_recipients', '[]'),
       ('alerts.whatsapp_template', '"truckgate_alert"'),
       ('alerts.whatsapp_template_language', '"ar"'),
       ('alerts.whatsapp_min_severity', '"MEDIUM"'),
       ('alerts.whatsapp_photos', 'false'),
       ('alerts.whatsapp_api_version', '"v26.0"');
