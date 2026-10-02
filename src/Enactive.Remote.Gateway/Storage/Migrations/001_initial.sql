-- Protocol 2 (Plans/remote-e2e-design.md, D2): a fresh schema, no data carried over. Every private row
-- names its owner, and composite foreign keys make a child agree with its parent's owner, so a bug that
-- forgets a filter on a WRITE is refused by the database. They do not protect a SELECT that forgets one:
-- the isolation tests do. Each statement is safe to run twice: MySQL commits DDL implicitly, so a
-- migration stopped half-way is re-run from the top by the Migrator.

-- Every table names its charset and collation. A column that does not name one (display_name, label)
-- takes the DATABASE default, and a database created with another default (latin1 is still the default
-- of some servers) would turn a person's non-ASCII name into question marks without an error.

CREATE TABLE IF NOT EXISTS users (
  id               CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  display_name     VARCHAR(100) NOT NULL,
  status           VARCHAR(20) CHARACTER SET ascii NOT NULL,          -- Active | Disabled
  security_version INT         NOT NULL DEFAULT 1,
  sealed_bytes     BIGINT      NOT NULL DEFAULT 0,                     -- storage limit (Task 8.1)
  created_at       DATETIME(3) NOT NULL,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS external_identities (
  provider   VARCHAR(20)  CHARACTER SET ascii NOT NULL,                 -- github | google
  subject    VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  user_id    CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  display    VARCHAR(200) NOT NULL,
  created_at DATETIME(3)  NOT NULL,
  PRIMARY KEY (provider, subject),
  KEY ix_identities_user (user_id),
  CONSTRAINT fk_identities_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS admissions (
  provider     VARCHAR(20)  CHARACTER SET ascii NOT NULL,
  subject      VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  state        VARCHAR(20)  CHARACTER SET ascii NOT NULL,               -- Waiting | Approved | Refused
  display      VARCHAR(200) NOT NULL,
  requested_at DATETIME(3)  NOT NULL,
  decided_at   DATETIME(3)  NULL,
  PRIMARY KEY (provider, subject)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS user_sessions (
  id               CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  user_id          CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  security_version INT         NOT NULL,
  created_at       DATETIME(3) NOT NULL,
  expires_at       DATETIME(3) NOT NULL,
  revoked_at       DATETIME(3) NULL,
  PRIMARY KEY (id),
  KEY ix_sessions_user (user_id, revoked_at),
  CONSTRAINT fk_sessions_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS user_streams (
  owner_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  value    BIGINT   NOT NULL DEFAULT 0,
  epoch    INT      NOT NULL DEFAULT 1,
  PRIMARY KEY (owner_id),
  CONSTRAINT fk_streams_user FOREIGN KEY (owner_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS user_retention (
  owner_id       CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  trimmed_before DATETIME(3) NULL,
  trimmed_at     DATETIME(3) NULL,
  PRIMARY KEY (owner_id),
  CONSTRAINT fk_retention_user FOREIGN KEY (owner_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS devices (
  id           CHAR(32)      CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  owner_id     CHAR(32)      CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  public_key   VARBINARY(65) NOT NULL,
  label        VARCHAR(80)   NOT NULL,
  created_at   DATETIME(3)   NOT NULL,
  last_seen_at DATETIME(3)   NULL,
  revoked_at   DATETIME(3)   NULL,
  PRIMARY KEY (id),
  UNIQUE KEY ux_devices_owner (owner_id, id),
  CONSTRAINT fk_devices_user FOREIGN KEY (owner_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS hosts (
  id             CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  owner_id       CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  label          VARCHAR(80)  NOT NULL,                                   -- plaintext by design (spec §6)
  token_hash     CHAR(64)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  key_epoch      INT UNSIGNED NOT NULL DEFAULT 0,
  signing_public VARBINARY(65) NULL,                                      -- the computer's signing key, pinned by its first grant
  revoked        TINYINT(1)   NOT NULL DEFAULT 0,
  last_seen_at   DATETIME(3)  NULL,
  created_at     DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY ux_hosts_token (token_hash),
  UNIQUE KEY ux_hosts_owner (owner_id, id),
  KEY ix_hosts_owner_created (owner_id, created_at),
  CONSTRAINT fk_hosts_user FOREIGN KEY (owner_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS host_workspaces (
  owner_id     CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  host_id      CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  workspace_id VARCHAR(100) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  sealed_name  TEXT         CHARACTER SET ascii NOT NULL,
  PRIMARY KEY (host_id, workspace_id),
  KEY ix_workspaces_owner (owner_id, host_id),
  CONSTRAINT fk_workspaces_host FOREIGN KEY (owner_id, host_id) REFERENCES hosts (owner_id, id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS grants (
  owner_id   CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  host_id    CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  device_id  CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  epoch      INT UNSIGNED NOT NULL,
  grant_json TEXT         CHARACTER SET ascii NOT NULL,
  created_at DATETIME(3)  NOT NULL,
  PRIMARY KEY (host_id, device_id, epoch),
  KEY ix_grants_device (owner_id, device_id),
  CONSTRAINT fk_grants_host   FOREIGN KEY (owner_id, host_id)   REFERENCES hosts (owner_id, id)   ON DELETE CASCADE,
  CONSTRAINT fk_grants_device FOREIGN KEY (owner_id, device_id) REFERENCES devices (owner_id, id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS invites (
  id                CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  owner_id          CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  created_by_host   CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NULL,
  created_by_device CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NULL,
  created_at        DATETIME(3) NOT NULL,
  expires_at        DATETIME(3) NOT NULL,
  consumed_at       DATETIME(3) NULL,
  -- The owner leads the key, as for tasks and commands: the id is the caller's own making, and with a
  -- key on the id alone Bob making an invitation under an id of Alice's was refused as taken - an answer
  -- that told him the id was somebody's - and his insert waited on her row while it found that out.
  PRIMARY KEY (owner_id, id),
  CONSTRAINT fk_invites_user FOREIGN KEY (owner_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS enrollments (
  invite_id  CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  owner_id   CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  device_id  CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  mac        VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  created_at DATETIME(3) NOT NULL,
  answered_at DATETIME(3) NULL,                                           -- the inviting computer has handled it
  PRIMARY KEY (owner_id, invite_id),
  CONSTRAINT fk_enrollments_invite FOREIGN KEY (owner_id, invite_id) REFERENCES invites (owner_id, id) ON DELETE CASCADE,
  CONSTRAINT fk_enrollments_device FOREIGN KEY (owner_id, device_id) REFERENCES devices (owner_id, id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS tasks (
  owner_id     CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  id           CHAR(36)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,  -- browser-made UUID: it is in the AD
  host_id      CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  workspace_id VARCHAR(100) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  sealed       MEDIUMTEXT   CHARACTER SET ascii NOT NULL,
  fingerprint  CHAR(64)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  created_at   DATETIME(3)  NOT NULL,
  PRIMARY KEY (owner_id, id),
  UNIQUE KEY ux_tasks_owner_host (owner_id, host_id, id),
  KEY ix_tasks_owner_created (owner_id, created_at),
  CONSTRAINT fk_tasks_host FOREIGN KEY (owner_id, host_id) REFERENCES hosts (owner_id, id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS runs (
  id               CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  owner_id         CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  task_id          CHAR(36)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  host_id          CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  status           VARCHAR(20) CHARACTER SET ascii NOT NULL,
  applied_sequence BIGINT      NOT NULL DEFAULT 0,
  created_at       DATETIME(3) NOT NULL,
  ended_at         DATETIME(3) NULL,
  sealed_summary   MEDIUMTEXT  CHARACTER SET ascii NULL,                 -- the terminal event's envelope, copied
  summary_sequence BIGINT      NULL,                                     -- that event's sequence: the panel rebuilds its AD
  PRIMARY KEY (id),
  UNIQUE KEY ux_runs_owner (owner_id, id),
  UNIQUE KEY ux_runs_owner_host (owner_id, host_id, id),
  KEY ix_runs_owner_task (owner_id, task_id, created_at),
  KEY ix_runs_owner_created (owner_id, created_at),
  -- What a start counts its account's active runs through, under the account's lock. Without it the count
  -- read every run the account ever had, on every start, while the account's other calls waited.
  KEY ix_runs_owner_status (owner_id, status),
  -- What retention removes ended runs through, the longest-ended first.
  KEY ix_runs_owner_ended (owner_id, ended_at),
  CONSTRAINT fk_runs_task FOREIGN KEY (owner_id, host_id, task_id) REFERENCES tasks (owner_id, host_id, id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS commands (
  owner_id    CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  id          VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  host_id     CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  run_id      CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NULL,      -- the run it is about; retention removes it with the run
  kind        VARCHAR(20) CHARACTER SET ascii NOT NULL,
  payload     MEDIUMTEXT  CHARACTER SET ascii NOT NULL,
  fingerprint CHAR(64)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  status      VARCHAR(20) CHARACTER SET ascii NOT NULL,
  created_at  DATETIME(3) NOT NULL,
  expires_at  DATETIME(3) NOT NULL,
  PRIMARY KEY (owner_id, id),
  KEY ix_commands_delivery (host_id, status, created_at),
  KEY ix_commands_expiry (status, expires_at),
  KEY ix_commands_owner_run (owner_id, run_id),
  CONSTRAINT fk_commands_host FOREIGN KEY (owner_id, host_id) REFERENCES hosts (owner_id, id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS approvals (
  owner_id           CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  host_id            CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  id                 VARCHAR(100) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  run_id             CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  tool_call_id       VARCHAR(100) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  action_hash        CHAR(64)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  remote_decidable   TINYINT(1)   NOT NULL,
  sealed_action      MEDIUMTEXT   CHARACTER SET ascii NOT NULL,
  status             VARCHAR(20)  CHARACTER SET ascii NOT NULL,
  requested_decision VARCHAR(10)  CHARACTER SET ascii NULL,
  created_at         DATETIME(3)  NOT NULL,
  expires_at         DATETIME(3)  NOT NULL,
  PRIMARY KEY (host_id, id),
  -- What a person's locking lookup goes through. Through the primary key, Bob asking for Alice's request
  -- locked Alice's row before the owner filter refused it: he waited on her, and the wait told him it exists.
  UNIQUE KEY ux_approvals_owner_host (owner_id, host_id, id),
  KEY ix_approvals_owner (owner_id, status, created_at),
  KEY ix_approvals_run (run_id, status),
  KEY ix_approvals_expiry (status, expires_at),
  CONSTRAINT fk_approvals_run FOREIGN KEY (owner_id, host_id, run_id) REFERENCES runs (owner_id, host_id, id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS events (
  owner_id      CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  host_id       CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  id            VARCHAR(100) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  run_id        CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  sequence      BIGINT       NOT NULL,
  kind          VARCHAR(20)  CHARACTER SET ascii NOT NULL,
  sealed_detail MEDIUMTEXT   CHARACTER SET ascii NULL,
  at            DATETIME(3)  NOT NULL,
  ordinal       BIGINT       NOT NULL,
  PRIMARY KEY (host_id, id),
  UNIQUE KEY ux_events_run_sequence (run_id, sequence),
  UNIQUE KEY ux_events_owner_ordinal (owner_id, ordinal),
  KEY ix_events_owner_at (owner_id, at),
  CONSTRAINT fk_events_run FOREIGN KEY (owner_id, host_id, run_id) REFERENCES runs (owner_id, host_id, id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS notices (
  id            CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  owner_id      CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  -- The run it is about, or - for a removal or an endorsement that never reached its computer, which is about
  -- no run - the computer. One of the two is set; each takes the notice with it when it is deleted.
  run_id        CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NULL,
  host_id       CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NULL,
  kind          VARCHAR(40) CHARACTER SET ascii NOT NULL,              -- metadata: the gateway's own words
  sealed_detail MEDIUMTEXT  CHARACTER SET ascii NULL,                  -- the event's envelope, copied
  event_sequence BIGINT     NULL,                                      -- with what the panel needs to rebuild
  event_kind    VARCHAR(20) CHARACTER SET ascii NULL,                  -- that event's associated data
  at            DATETIME(3) NOT NULL,
  is_read       TINYINT(1)  NOT NULL DEFAULT 0,
  ordinal       BIGINT      NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY ux_notices_owner_ordinal (owner_id, ordinal),
  KEY ix_notices_owner_unread (owner_id, is_read, at),
  -- What retention deletes through, a person's oldest first. Without it the per-owner DELETE read and
  -- locked all of that person's notices to find the oldest thousand.
  KEY ix_notices_owner_at (owner_id, at),
  KEY ix_notices_owner_host (owner_id, host_id),
  CONSTRAINT fk_notices_run FOREIGN KEY (owner_id, run_id) REFERENCES runs (owner_id, id) ON DELETE CASCADE,
  CONSTRAINT fk_notices_host FOREIGN KEY (owner_id, host_id) REFERENCES hosts (owner_id, id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS audit (
  id       BIGINT      NOT NULL AUTO_INCREMENT,
  owner_id CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin NULL,
  at       DATETIME(3) NOT NULL,
  actor    VARCHAR(80) CHARACTER SET ascii NOT NULL,                  -- user:<id> | host:<id> | device:<id> | operator
  action   VARCHAR(40) CHARACTER SET ascii NOT NULL,
  -- Binary, like the subjects it names: deleting an account removes the operator's rows by their target, and
  -- compared without regard to case, deleting dev:bob-x also removed the record about dev:Bob-x, someone else.
  target   VARCHAR(100) CHARACTER SET ascii COLLATE ascii_bin NULL,
  PRIMARY KEY (id),
  KEY ix_audit_owner (owner_id, at),
  CONSTRAINT fk_audit_user FOREIGN KEY (owner_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
