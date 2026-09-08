-- Enactive Remote gateway, migration 002: a cursor the panel can poll with, and a retention mark.
-- See Docs/REMOTE_DESIGN.md section 4.3.
--
-- The panel polls. Re-sending every event on every poll is what made the preview's cost grow with
-- its history, so a poll has to be able to say "only what I have not seen". That needs a cursor,
-- and the first cursor this gateway shipped was the server's wall clock - which loses rows.
--
-- A transaction that began before the poll and commits after it writes a row whose `at` is EARLIER
-- than the cursor the panel just stored. The panel asks for "newer than that" and the row is never
-- delivered, once, silently, for ever. For an ApprovalRequested notice that is a permission nobody
-- is ever asked for.
--
-- So the cursor is a number this gateway hands out, from ONE counter shared by both streams, taken
-- under a row lock held to commit. Because a transaction cannot allocate until the previous holder
-- commits, allocation order IS commit order: seeing ordinal N proves every ordinal below it is
-- already committed and visible. That is the whole property the delta rests on.
--
-- The cost is that every event and every notice serialises on one row at the moment it is written.
-- For a gateway serving one person and their own computers that is nothing, and it buys an
-- invariant rather than a probability.

CREATE TABLE IF NOT EXISTS counters (
  name   VARCHAR(40) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  value  BIGINT      NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO counters (name, value) VALUES ('stream', 0);

-- What the retention job has already thrown away. NULL means nothing has been trimmed yet; a
-- timestamp means the panel must say history before it is gone rather than let a short list read
-- as a quiet week.
CREATE TABLE IF NOT EXISTS retention_state (
  id              TINYINT      NOT NULL PRIMARY KEY,
  trimmed_before  DATETIME(3)  NULL,
  trimmed_at      DATETIME(3)  NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO retention_state (id, trimmed_before, trimmed_at) VALUES (1, NULL, NULL);

-- MySQL 8 has no ADD COLUMN IF NOT EXISTS - that is MariaDB - and this file has to survive being
-- run twice, because DDL commits implicitly and a failure half way through leaves the version row
-- unwritten. So each schema change is prepared from a string that is a no-op when the change is
-- already there. Verbose, and the alternative is a migration that can only ever be run once on a
-- database that has already had it partly applied.

SET @ddl := IF((SELECT COUNT(*) FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'events' AND COLUMN_NAME = 'ordinal') = 0,
               'ALTER TABLE events ADD COLUMN ordinal BIGINT NOT NULL DEFAULT 0',
               'DO 0');
PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @ddl := IF((SELECT COUNT(*) FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'notices' AND COLUMN_NAME = 'ordinal') = 0,
               'ALTER TABLE notices ADD COLUMN ordinal BIGINT NOT NULL DEFAULT 0',
               'DO 0');
PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- Rows written before this migration existed. They are numbered by time so the panel reads them in
-- the order they happened; the two tables are offset so they share one number line, because they
-- are one change stream that happens to be stored twice. `WHERE ordinal = 0` is what makes a
-- second run a no-op instead of a renumbering.
UPDATE events AS e
JOIN (SELECT host_id, id, ROW_NUMBER() OVER (ORDER BY at, id) AS n FROM events) AS o
  ON o.host_id = e.host_id AND o.id = e.id
SET e.ordinal = o.n
WHERE e.ordinal = 0;

UPDATE notices AS n
JOIN (SELECT id, ROW_NUMBER() OVER (ORDER BY at, id) AS r FROM notices) AS o ON o.id = n.id
JOIN (SELECT COUNT(*) AS c FROM events) AS e
SET n.ordinal = o.r + e.c
WHERE n.ordinal = 0;

UPDATE counters
SET value = GREATEST(value,
                     (SELECT COALESCE(MAX(ordinal), 0) FROM events),
                     (SELECT COALESCE(MAX(ordinal), 0) FROM notices))
WHERE name = 'stream';

-- UNIQUE, not merely indexed. Two rows sharing an ordinal would both be delivered in the same
-- window and neither would be wrong to be there, so the mistake would never show as a symptom -
-- it would show as a panel that is subtly out of order, months later. The database refusing it is
-- the only place that stays cheap.
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'events'
                  AND INDEX_NAME = 'ux_events_ordinal') = 0,
               'ALTER TABLE events ADD UNIQUE KEY ux_events_ordinal (ordinal)',
               'DO 0');
PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @ddl := IF((SELECT COUNT(*) FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'notices'
                  AND INDEX_NAME = 'ux_notices_ordinal') = 0,
               'ALTER TABLE notices ADD UNIQUE KEY ux_notices_ordinal (ordinal)',
               'DO 0');
PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- And now the default goes away, which is the point of having added it.
--
-- With DEFAULT 0 in place, a write path that forgot to allocate an ordinal would insert 0 and
-- SUCCEED - once. The row would sit outside every delta window for ever and the second such row
-- would be the one that failed, blaming a bug that had already happened. Without a default, the
-- first one fails at the insert, which is where it can still be read as what it is.
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'events'
                  AND COLUMN_NAME = 'ordinal' AND COLUMN_DEFAULT IS NOT NULL) = 1,
               'ALTER TABLE events ALTER COLUMN ordinal DROP DEFAULT',
               'DO 0');
PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @ddl := IF((SELECT COUNT(*) FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'notices'
                  AND COLUMN_NAME = 'ordinal' AND COLUMN_DEFAULT IS NOT NULL) = 1,
               'ALTER TABLE notices ALTER COLUMN ordinal DROP DEFAULT',
               'DO 0');
PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- Trimming deletes by age, and the delta reads by ordinal; both need to be cheap without the other.
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'events'
                  AND INDEX_NAME = 'ix_events_at') = 0,
               'ALTER TABLE events ADD KEY ix_events_at (at)',
               'DO 0');
PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;
