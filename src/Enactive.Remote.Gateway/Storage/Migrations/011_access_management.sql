-- Admission revisions reject stale decisions, including a state that changed away and back.
-- Repeatable because MySQL commits DDL even when a later statement in the migration fails.
SET @missing := (SELECT COUNT(*) = 0 FROM information_schema.columns
  WHERE table_schema = DATABASE() AND table_name = 'admissions' AND column_name = 'revision');
SET @ddl := IF(@missing, 'ALTER TABLE admissions ADD COLUMN revision INT NOT NULL DEFAULT 0', 'DO 0');
PREPARE admin_change FROM @ddl;
EXECUTE admin_change;
DEALLOCATE PREPARE admin_change;

SET @missing := (SELECT COUNT(*) = 0 FROM information_schema.columns
  WHERE table_schema = DATABASE() AND table_name = 'administrator_audit' AND column_name = 'detail');
SET @ddl := IF(@missing, 'ALTER TABLE administrator_audit ADD COLUMN detail JSON NULL', 'DO 0');
PREPARE admin_change FROM @ddl;
EXECUTE admin_change;
DEALLOCATE PREPARE admin_change;

-- Provider plus subject can take 276 ASCII characters. Never truncate the identity being decided.
ALTER TABLE administrator_audit MODIFY COLUMN target VARCHAR(276) CHARACTER SET ascii COLLATE ascii_bin NOT NULL;
