-- Enactive Remote gateway, initial schema. See Docs/REMOTE_DESIGN.md section 4.2.
--
-- Every statement is written to be safe to run twice. MySQL commits DDL implicitly, so a migration
-- CANNOT be rolled back half-way: if one of these fails, the ones before it have already happened
-- and the version row was never written. Re-running is then the recovery, which only works if
-- re-running is harmless - hence IF NOT EXISTS on everything.
--
-- Identifiers and hashes are `ascii` + `_bin`: they are byte strings, not language. A case- and
-- accent-insensitive comparison on a token hash is a comparison that says two different hashes are
-- equal, and `WHERE token_hash = ?` is the check that authenticates a device.
--
-- Text the model or the owner writes is `utf8mb4`. Where the protocol caps a field, the column is
-- MEDIUMTEXT rather than TEXT: 16 000 characters of utf8mb4 is up to 64 000 bytes and TEXT holds
-- 65 535, which is a margin too thin to rely on.

CREATE TABLE IF NOT EXISTS schema_version (
  version     INT          NOT NULL PRIMARY KEY,
  applied_at  DATETIME(3)  NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS hosts (
  id            CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  name          VARCHAR(80)  NOT NULL,
  token_hash    CHAR(64)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  revoked       TINYINT(1)   NOT NULL DEFAULT 0,
  last_seen_at  DATETIME(3)  NULL,
  created_at    DATETIME(3)  NOT NULL,
  -- Authentication is one indexed lookup on this. The preview deserialised the entire application
  -- state on every authenticated request instead, including every SignalR negotiate.
  UNIQUE KEY ux_hosts_token (token_hash)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Workspaces are published by the Host and replaced wholesale on every Sync. There is no path
-- column and there is not going to be one: the remote side names a workspace the Host already has,
-- and cannot name a folder.
CREATE TABLE IF NOT EXISTS host_workspaces (
  host_id       CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  workspace_id  VARCHAR(100) NOT NULL,
  name          VARCHAR(100) NOT NULL,
  PRIMARY KEY (host_id, workspace_id),
  CONSTRAINT fk_workspaces_host FOREIGN KEY (host_id) REFERENCES hosts (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS tasks (
  id            CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  host_id       CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  workspace_id  VARCHAR(100) NOT NULL,
  title         VARCHAR(140) NOT NULL,
  prompt        MEDIUMTEXT   NOT NULL,
  created_at    DATETIME(3)  NOT NULL,
  KEY ix_tasks_host (host_id, created_at),
  CONSTRAINT fk_tasks_host FOREIGN KEY (host_id) REFERENCES hosts (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS runs (
  id                CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  task_id           CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  host_id           CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  status            VARCHAR(20)  CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  -- The highest event sequence applied to this run. Read and written under SELECT ... FOR UPDATE,
  -- which is what serialises two events arriving at once.
  applied_sequence  BIGINT       NOT NULL DEFAULT 0,
  created_at        DATETIME(3)  NOT NULL,
  ended_at          DATETIME(3)  NULL,
  summary           MEDIUMTEXT   NULL,
  KEY ix_runs_host_status (host_id, status),
  KEY ix_runs_task (task_id, created_at),
  CONSTRAINT fk_runs_task FOREIGN KEY (task_id) REFERENCES tasks (id),
  CONSTRAINT fk_runs_host FOREIGN KEY (host_id) REFERENCES hosts (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- One instruction from the owner. `id` is supplied by the caller so a retried POST cannot queue the
-- same action twice; `fingerprint` is what the id was first used for, so re-using an id for a
-- DIFFERENT action is a conflict rather than a silent no-op.
CREATE TABLE IF NOT EXISTS commands (
  -- VARCHAR and not CHAR(36), which is the shape of a UUID: MySqlConnector treats a CHAR(36)
  -- column as a Guid unless told otherwise, and this id is a string the owner supplied and that we
  -- only ever compare as text. The driver is also told GuidFormat=None - see Database - so neither
  -- half of this depends on the other being remembered.
  id           VARCHAR(36)  CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  host_id      CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  kind         VARCHAR(20)  CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  payload      MEDIUMTEXT   NOT NULL,
  fingerprint  CHAR(64)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  status       VARCHAR(20)  CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  created_at   DATETIME(3)  NOT NULL,
  expires_at   DATETIME(3)  NOT NULL,
  KEY ix_commands_delivery (host_id, status, created_at),
  KEY ix_commands_expiry (status, expires_at),
  CONSTRAINT fk_commands_host FOREIGN KEY (host_id) REFERENCES hosts (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- A permission request, as the owner will see it. `arguments` is the complete action and not a
-- summary: a person cannot approve what they were not shown.
--
-- `remote_decidable` is 0 for a shell. The request is still stored and still displayed, but the
-- resolve endpoint refuses it - the boundary is here, on the server, not in whether the panel drew
-- a button.
CREATE TABLE IF NOT EXISTS approvals (
  id                 VARCHAR(100) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  host_id            CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  run_id             CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  tool_call_id       VARCHAR(100) NOT NULL,
  tool               VARCHAR(200) NOT NULL,
  arguments          MEDIUMTEXT   NOT NULL,
  working_directory  VARCHAR(1000) NOT NULL,
  reason             MEDIUMTEXT   NOT NULL,
  action_hash        CHAR(64)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  remote_decidable   TINYINT(1)   NOT NULL,
  status             VARCHAR(20)  CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  requested_decision VARCHAR(10)  CHARACTER SET ascii COLLATE ascii_bin NULL,
  created_at         DATETIME(3)  NOT NULL,
  expires_at         DATETIME(3)  NOT NULL,
  KEY ix_approvals_run (run_id, status),
  KEY ix_approvals_expiry (status, expires_at),
  CONSTRAINT fk_approvals_run FOREIGN KEY (run_id) REFERENCES runs (id),
  CONSTRAINT fk_approvals_host FOREIGN KEY (host_id) REFERENCES hosts (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- What the Host has told us. Two keys here do work the code would otherwise have to remember:
--
--   PRIMARY KEY (host_id, id) - a Host cannot collide with another Host's event id, and isolation
--   is structural rather than a WHERE clause somebody must not forget.
--
--   UNIQUE (run_id, sequence) - an event applied twice, or out of order, is refused by the database
--   itself. Deduplication by id alone stops the first and does nothing about the second: a retried
--   event landing after a later one would drive the run's state backwards.
CREATE TABLE IF NOT EXISTS events (
  id        VARCHAR(100) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  host_id   CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  run_id    CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  sequence  BIGINT       NOT NULL,
  kind      VARCHAR(20)  CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  detail    MEDIUMTEXT   NULL,
  at        DATETIME(3)  NOT NULL,
  PRIMARY KEY (host_id, id),
  UNIQUE KEY ux_events_run_sequence (run_id, sequence),
  KEY ix_events_run_at (run_id, at),
  CONSTRAINT fk_events_run FOREIGN KEY (run_id) REFERENCES runs (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS notices (
  id       CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  run_id   CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  title    VARCHAR(120) NOT NULL,
  detail   MEDIUMTEXT   NOT NULL,
  at       DATETIME(3)  NOT NULL,
  is_read  TINYINT(1)   NOT NULL DEFAULT 0,
  KEY ix_notices_unread (is_read, at),
  CONSTRAINT fk_notices_run FOREIGN KEY (run_id) REFERENCES runs (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
