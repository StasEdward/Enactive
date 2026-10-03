-- Versions 2-9 are deliberately unused: protocol 1 used schema version 2, which must remain
-- foreign to this gateway. Recognising it as our next migration would accept an incompatible DB.
CREATE TABLE IF NOT EXISTS administrators (
  id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  issuer VARCHAR(512) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  subject VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  enabled BOOLEAN NOT NULL,
  security_version INT NOT NULL DEFAULT 1,
  created_at DATETIME(3) NOT NULL,
  updated_at DATETIME(3) NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY ux_administrator_identity (issuer, subject)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS administrator_sessions (
  id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  administrator_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  security_version INT NOT NULL,
  authenticated_at DATETIME(3) NOT NULL,
  expires_at DATETIME(3) NOT NULL,
  revoked_at DATETIME(3) NULL,
  PRIMARY KEY (id),
  KEY ix_admin_sessions_owner (administrator_id, revoked_at),
  KEY ix_admin_sessions_expiry (expires_at),
  CONSTRAINT fk_admin_session FOREIGN KEY (administrator_id) REFERENCES administrators(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- No FK to ordinary users: deleting an account must not delete the operator's security history.
-- IDs only, no provider claims, tokens, IP addresses or task content. Retained for 90 days.
CREATE TABLE IF NOT EXISTS administrator_audit (
  id BIGINT NOT NULL AUTO_INCREMENT,
  at DATETIME(3) NOT NULL,
  actor VARCHAR(80) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  action VARCHAR(40) CHARACTER SET ascii NOT NULL,
  target CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PRIMARY KEY (id),
  KEY ix_admin_audit_at (at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
