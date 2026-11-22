-- Wrapper used by the Windows installer: passwords come from environment variables
-- (TG_OWNER_PW, TG_APP_PW), never from the command line.
\set ON_ERROR_STOP 1
\getenv owner_pw TG_OWNER_PW
\getenv app_pw TG_APP_PW
\ir db-setup.sql
