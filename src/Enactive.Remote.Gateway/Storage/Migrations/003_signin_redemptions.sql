-- Signing in with a provider redeems its answer once, and not after the account signed out everywhere
-- (ExternalSignIn.CompleteAsync, SessionStore). The answer rides to /auth/complete in the External cookie, a
-- protected ticket good for ten minutes; deleting the browser's copy did not end it, so a copy kept by anybody
-- could be redeemed again for a new session - also after "Sign out everywhere", without the provider.
--
-- Numbered 3, and there is no 2 on purpose: the protocol-1 database, kept beside this one as the rollback,
-- records versions 1 and 2. A 002 here would make that database read as up to date, and the Migrator's refusal
-- of another protocol's database would let a gateway pointed at it by mistake start on tables of another shape.
--
-- Each statement is safe to run twice, as 001's are: MySQL commits DDL implicitly, so a migration stopped
-- half-way is re-run from the top.

-- One row per answer redeemed, keyed by the random id the answer was issued with. The key is what makes a
-- second redemption fail, sequential or at once: the insert is the first thing a redemption does, whatever it
-- then comes to, and the loser's insert waits on the winner's row and fails on the duplicate. Owned by nobody - the id is random
-- and names no person - and removed a day after it was written (Retention), long after its answer expired.
CREATE TABLE IF NOT EXISTS signin_redemptions (
  id          CHAR(64)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  redeemed_at DATETIME(3) NOT NULL,
  PRIMARY KEY (id),
  KEY ix_redemptions_redeemed (redeemed_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- When the account's sessions were last ended all at once: signing out everywhere, the operator's revoke, a
-- disablement. Set in the statement that moves security_version on. An answer issued at or before it opens no
-- session: the version alone cannot say that, because the answer is made before anybody knows the account.
--
-- MySQL 8.0 has no ADD COLUMN IF NOT EXISTS, so the column is added only when information_schema says it is
-- missing; the statement run otherwise does nothing.
SET @sessions_revoked_at_missing := (
  SELECT COUNT(*) = 0 FROM information_schema.columns
  WHERE table_schema = DATABASE() AND table_name = 'users' AND column_name = 'sessions_revoked_at');

SET @add_sessions_revoked_at := IF(@sessions_revoked_at_missing,
  'ALTER TABLE users ADD COLUMN sessions_revoked_at DATETIME(3) NULL',
  'DO 0');

PREPARE add_sessions_revoked_at FROM @add_sessions_revoked_at;
EXECUTE add_sessions_revoked_at;
DEALLOCATE PREPARE add_sessions_revoked_at;
