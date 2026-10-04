-- Keep a defaults row even before any override exists: locking an absent row would
-- not serialize a default edit with creations on every Gateway instance.
CREATE TABLE IF NOT EXISTS quota_defaults (
  id INT NOT NULL PRIMARY KEY,
  revision INT NOT NULL DEFAULT 0,
  settings JSON NOT NULL,
  CONSTRAINT ck_quota_defaults_singleton CHECK (id = 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
INSERT IGNORE INTO quota_defaults (id, settings) VALUES (1, JSON_OBJECT());

-- Resetting retains the revision; deleting the row would let an old revision-zero
-- form overwrite a later edit. Account deletion still removes its overrides.
CREATE TABLE IF NOT EXISTS user_quotas (
  owner_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  revision INT NOT NULL DEFAULT 0,
  settings JSON NOT NULL,
  FOREIGN KEY (owner_id) REFERENCES users(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
