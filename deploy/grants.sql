-- The accounts the gateway and its backups run as. Applied by a DBA, once, by hand.
--
-- The account the TESTS use may create and drop databases, because they create one per test class.
-- The account the GATEWAY uses may not create anything: it has rights on one schema and nothing
-- else, so a compromised gateway cannot reach another database on the same server, and a bug in a
-- migration cannot invent a schema nobody meant to have.
--
-- Passwords are not in this file and must not be added to it. Set them when creating the accounts,
-- from a shell that is not logging history, and put the gateway's into
-- /etc/enactive-remote/gateway.env - which is the only place it belongs.

-- ── the gateway ─────────────────────────────────────────────────────────────
--
-- CREATE, ALTER, INDEX and DROP look generous for a service account and are not optional: the
-- gateway applies its own migrations at startup, and every one of those is DDL on this schema. It
-- is scoped to this schema alone, which is the part that matters.
--
-- CREATE ROUTINE and friends are absent on purpose. Migrations do not define routines, and the day
-- one wants to, that is a decision someone should have to make here rather than discover has
-- already been possible.

CREATE USER IF NOT EXISTS 'enactive_gateway'@'localhost' IDENTIFIED BY 'set-me';

GRANT SELECT, INSERT, UPDATE, DELETE,
      CREATE, ALTER, INDEX, DROP, REFERENCES
  ON `enactive_remote`.*
  TO 'enactive_gateway'@'localhost';

-- ── backups ─────────────────────────────────────────────────────────────────
--
-- Read, and the two rights mysqldump needs to take a consistent snapshot without locking the
-- gateway out of its own tables. Nothing that can change a row: a backup account that can write is
-- a backup account that can destroy what it was meant to protect.
--
-- CREATE and DROP on the verify schemas only, because verify-restore.sh restores into a scratch
-- database and drops it - and a backup nobody has ever restored is a cron entry with a good
-- reputation, not a backup.

CREATE USER IF NOT EXISTS 'enactive_backup'@'localhost' IDENTIFIED BY 'set-me';

GRANT SELECT, LOCK TABLES, SHOW VIEW, EVENT, TRIGGER
  ON `enactive_remote`.*
  TO 'enactive_backup'@'localhost';

GRANT SELECT, INSERT, CREATE, ALTER, INDEX, DROP, REFERENCES
  ON `enactive\_remote\_verify\_%`.*
  TO 'enactive_backup'@'localhost';

-- Needed for --single-transaction against a server with binary logging on, and for nothing else.
GRANT RELOAD ON *.* TO 'enactive_backup'@'localhost';

FLUSH PRIVILEGES;
