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
--
-- The schema is enactive_remote_v2, protocol 2's. The protocol-1 schema, enactive_remote, keeps the
-- grants it was given when it was made: it is the rollback, and this file does not touch it.

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
  ON `enactive_remote_v2`.*
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
  ON `enactive_remote_v2`.*
  TO 'enactive_backup'@'localhost';

-- Everything a restore of THIS dump can need, on scratch schemas this account creates and drops.
--
-- The list is wide because the rule is: verify-restore.sh must restore the same dump you would
-- restore in anger. The first version was narrower and the first real run failed on LOCK TABLES -
-- mysqldump wraps its inserts in them by default, and restoring that needs the privilege on the
-- TARGET. The tempting fix was to pass --skip-add-locks and make the dump fit the account. That
-- would have verified a different artifact from the one kept, which is the one thing this job
-- exists not to do.
--
-- CREATE VIEW, CREATE ROUTINE, TRIGGER and EVENT cover what the dump does not contain today. The
-- day somebody adds a view or a trigger, the backup keeps working and the verification does not
-- start failing with a permissions error that says nothing about the real cause.
GRANT SELECT, INSERT, CREATE, ALTER, INDEX, DROP, REFERENCES,
      LOCK TABLES, CREATE VIEW, SHOW VIEW, CREATE ROUTINE, ALTER ROUTINE, EXECUTE,
      TRIGGER, EVENT
  ON `enactive\_remote\_v2\_verify\_%`.*
  TO 'enactive_backup'@'localhost';

-- No RELOAD and no PROCESS, on purpose. Both are server-wide: RELOAD is not needed by
-- --single-transaction on its own, and PROCESS - which mysqldump 8.0 wants for the tablespace
-- query - would let this account watch every statement running on the machine. backup.sh passes
-- --no-tablespaces instead, which is the same result without the privilege.

FLUSH PRIVILEGES;
