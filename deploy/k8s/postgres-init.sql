-- Database per service on one PostgreSQL server: every service gets its own database and its own login,
-- and no login can connect to another service's database. Splitting onto separate servers later is a
-- connection-string change.

CREATE ROLE catalog LOGIN PASSWORD 'catalog';
CREATE DATABASE catalog OWNER catalog;
REVOKE ALL ON DATABASE catalog FROM PUBLIC;

CREATE ROLE booking LOGIN PASSWORD 'booking';
CREATE DATABASE booking OWNER booking;
REVOKE ALL ON DATABASE booking FROM PUBLIC;

CREATE ROLE payment LOGIN PASSWORD 'payment';
CREATE DATABASE payment OWNER payment;
REVOKE ALL ON DATABASE payment FROM PUBLIC;

CREATE ROLE notification LOGIN PASSWORD 'notification';
CREATE DATABASE notification OWNER notification;
REVOKE ALL ON DATABASE notification FROM PUBLIC;
