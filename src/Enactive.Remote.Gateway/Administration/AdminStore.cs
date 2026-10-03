namespace Enactive.Remote.Gateway.Administration;

using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

internal sealed record AdminSession(string AdministratorId, string SessionId, int Version,
    DateTimeOffset AuthenticatedAt, DateTimeOffset ExpiresAt);

/// <summary>Administrative authority is independent of ordinary user admission and E2E devices.</summary>
internal sealed class AdminStore(Database db, TimeProvider clock)
{
    // No sliding renewal: activity with a stolen cookie must not extend administrative access.
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(5);

    public Task<string> GrantAsync(AdminIdentity identity, CancellationToken ct)
        => db.InTransactionAsync<string>(async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            await c.ExecuteAsync(tx, """
                INSERT INTO administrators (id, issuer, subject, enabled, created_at, updated_at)
                VALUES (@id, @issuer, @subject, 1, @now, @now)
                ON DUPLICATE KEY UPDATE enabled = 1, security_version = security_version + 1, updated_at = @now
                """, ("@id", Ids.New()), ("@issuer", identity.Issuer), ("@subject", identity.Subject), ("@now", now));
            var id = (await c.ReadOneAsync(tx,
                "SELECT id FROM administrators WHERE issuer = @issuer AND subject = @subject FOR UPDATE",
                r => r.GetString(0), ("@issuer", identity.Issuer), ("@subject", identity.Subject)))!;
            // Re-grant is also recovery: never revive a cookie held before access was reset.
            await c.ExecuteAsync(tx, "UPDATE administrator_sessions SET revoked_at = @now WHERE administrator_id = @id AND revoked_at IS NULL",
                ("@id", id), ("@now", now));
            await AuditAsync(c, tx, "operator", "administrator.granted", id);
            return id;
        }, ct);

    public Task<bool> RevokeAsync(AdminIdentity identity, CancellationToken ct)
        => db.InTransactionAsync<bool>(async (c, tx) =>
        {
            var id = await c.ReadOneAsync(tx,
                "SELECT id FROM administrators WHERE issuer = @issuer AND subject = @subject FOR UPDATE",
                r => r.GetString(0), ("@issuer", identity.Issuer), ("@subject", identity.Subject));
            if (id is null) return false;
            var now = clock.GetUtcNow();
            await c.ExecuteAsync(tx,
                "UPDATE administrators SET enabled = 0, security_version = security_version + 1, updated_at = @now WHERE id = @id",
                ("@id", id), ("@now", now));
            await c.ExecuteAsync(tx, "UPDATE administrator_sessions SET revoked_at = @now WHERE administrator_id = @id AND revoked_at IS NULL",
                ("@id", id), ("@now", now));
            await AuditAsync(c, tx, "operator", "administrator.revoked", id);
            return true;
        }, ct);

    /// <summary>Called only after the OIDC handler has checked signature, issuer, audience, nonce and MFA.</summary>
    public Task<AdminSession?> OpenAsync(AdminIdentity identity, DateTimeOffset authenticatedAt, CancellationToken ct)
        => db.InTransactionAsync<AdminSession?>(async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            if (authenticatedAt > now.AddSeconds(60) || now - authenticatedAt > FreshWindow) return null;
            var account = await c.ReadOneAsync(tx,
                "SELECT id, enabled, security_version FROM administrators WHERE issuer = @issuer AND subject = @subject FOR UPDATE",
                r => (Id: r.GetString(0), Enabled: r.GetBoolean(1), Version: r.GetInt32(2)),
                ("@issuer", identity.Issuer), ("@subject", identity.Subject));
            if (account.Id is null || !account.Enabled) return null;
            var session = new AdminSession(account.Id, Ids.New(), account.Version, authenticatedAt, now.Add(Lifetime));
            await c.ExecuteAsync(tx, """
                INSERT INTO administrator_sessions (id, administrator_id, security_version, authenticated_at, expires_at)
                VALUES (@id, @admin, @version, @at, @expires)
                """, ("@id", session.SessionId), ("@admin", session.AdministratorId), ("@version", session.Version),
                ("@at", session.AuthenticatedAt), ("@expires", session.ExpiresAt));
            await AuditAsync(c, tx, "admin:" + account.Id, "session.opened", session.SessionId);
            return session;
        }, ct);

    public async Task<AdminSession?> FindAsync(string administratorId, string sessionId, int version, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.ReadOneAsync(null, """
            SELECT s.authenticated_at, s.expires_at FROM administrator_sessions s
            JOIN administrators a ON a.id = s.administrator_id
            WHERE s.id = @session AND s.administrator_id = @admin AND a.enabled = 1
              AND s.security_version = @version AND a.security_version = @version
              AND s.revoked_at IS NULL AND s.expires_at > @now
            """, r => new AdminSession(administratorId, sessionId, version, r.Utc("authenticated_at"), r.Utc("expires_at")),
            ("@session", sessionId), ("@admin", administratorId), ("@version", version), ("@now", clock.GetUtcNow()));
    }

    public Task CloseAsync(AdminSession session, CancellationToken ct)
        => db.InTransactionAsync(async (c, tx) =>
        {
            // Same account-first lock order as grant/revoke/open, including sign-out racing recovery.
            await c.ExecuteAsync(tx, "SELECT id FROM administrators WHERE id = @id FOR UPDATE", ("@id", session.AdministratorId));
            var count = await c.ExecuteAsync(tx,
                "UPDATE administrator_sessions SET revoked_at = @now WHERE id = @id AND administrator_id = @admin AND revoked_at IS NULL",
                ("@id", session.SessionId), ("@admin", session.AdministratorId), ("@now", clock.GetUtcNow()));
            if (count > 0) await AuditAsync(c, tx, "admin:" + session.AdministratorId, "session.closed", session.SessionId);
        }, ct);

    public async Task PruneAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        // Bound each statement so a neglected security log does not hold a large delete lock
        // throughout the first maintenance pass after an outage.
        var now = clock.GetUtcNow();
        while (!ct.IsCancellationRequested && await c.ExecuteAsync(null,
            "DELETE FROM administrator_sessions WHERE expires_at < @now ORDER BY expires_at LIMIT 1000", ("@now", now)) == 1000) { }
        while (!ct.IsCancellationRequested && await c.ExecuteAsync(null,
            "DELETE FROM administrator_audit WHERE at < @before ORDER BY at LIMIT 1000", ("@before", now.AddDays(-90))) == 1000) { }
    }

    private Task AuditAsync(MySqlConnection c, MySqlTransaction tx, string actor, string action, string target)
        => c.ExecuteAsync(tx, "INSERT INTO administrator_audit (at, actor, action, target) VALUES (@at, @actor, @action, @target)",
            ("@at", clock.GetUtcNow()), ("@actor", actor), ("@action", action), ("@target", target));
}
