-- TruckGate — one-time database setup. Run as a PostgreSQL superuser:
--
--   psql -U postgres -v owner_pw='...' -v app_pw='...' -f deploy/db-setup.sql
--
-- Creates two roles:
--   truckgate_owner  owns the database and runs migrations (used only at upgrade time)
--   truckgate_app    used by the running server: read + insert, update only on mutable
--                    tables, never DELETE/TRUNCATE (granted by the migrations)
-- Keep both passwords out of the repository.

\set ON_ERROR_STOP 1

SELECT format('CREATE ROLE truckgate_owner LOGIN PASSWORD %L', :'owner_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'truckgate_owner') \gexec

SELECT format('CREATE ROLE truckgate_app LOGIN PASSWORD %L', :'app_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'truckgate_app') \gexec

SELECT 'CREATE DATABASE truckgate OWNER truckgate_owner ENCODING ''UTF8'''
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'truckgate') \gexec

\connect truckgate
REVOKE ALL ON DATABASE truckgate FROM PUBLIC;
GRANT CONNECT ON DATABASE truckgate TO truckgate_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
