namespace Enactive.Remote.Gateway.Accounts;

using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Administration;
using System.Text.Json;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

internal sealed record AdminMutation(AdminSession Session, int ExpectedVersion);
internal sealed record WaitingAdmission(AdmissionIdentity Identity, string Display, DateTimeOffset RequestedAt);
internal sealed record AdmissionDecisionResult(bool HasAccount);
internal sealed record AccountDisabledResult(int WithdrawnCommands);

/// <summary>
/// Administrative operations shared by host adapters. They return data, never terminal output, so
/// a web adapter does not have to repeat the SQL or interpret the CLI's prose. This is a trusted
/// server-side service, not authorization: an HTTP adapter must authenticate the administrator first.
/// Web mutations recheck their live session under locks in the same transaction as the change.
/// </summary>
internal sealed class AdministrationService(Database db, TimeProvider clock)
{
    public async Task<IReadOnlyList<WaitingAdmission>> ListWaitingAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        return await connection.ReadAllAsync(null,
            """
            SELECT provider, subject, display, requested_at FROM admissions
            WHERE state = 'Waiting' ORDER BY requested_at, provider, subject
            """,
            reader =>
            {
                var text = $"{reader.GetString("provider")}:{reader.GetString("subject")}";
                if (!AdmissionIdentity.TryParse(text, out var identity))
                    throw new InvalidOperationException("A stored admission has an invalid provider identity.");
                return new WaitingAdmission(identity, reader.GetString("display"), reader.Utc("requested_at"));
            });
    }

    /// <summary>
    /// An operator can admit an identity before it asks. The result distinguishes an admission from
    /// an existing account: refusing the former does not disable the latter.
    /// </summary>
    public async Task<AdmissionDecisionResult> DecideAdmissionAsync(
        AdmissionIdentity identity, AdmissionState state, CancellationToken ct, AdminMutation? mutation = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        // The CLI used to limit this through its command switch. Keep the invariant here as well,
        // or a new adapter could persist Waiting or an undefined enum as an operator's decision.
        if (state is not (AdmissionState.Approved or AdmissionState.Refused))
            throw new ArgumentOutOfRangeException(nameof(state), "An admission decision must approve or refuse.");

        var hasAccount = await db.InTransactionAsync<bool>(async (connection, transaction) =>
        {
            await AuthorizeAsync(connection, transaction, mutation);
            var before = await connection.ReadOneAsync(transaction,
                "SELECT state, revision FROM admissions WHERE provider = @provider AND subject = @subject FOR UPDATE",
                r => (State: r.GetString(0), Version: r.GetInt32(1)), ("@provider", identity.Provider), ("@subject", identity.Subject));
            if (mutation is not null && before.State is null) throw GatewayFault.NotFound("Registration not found.");
            CheckVersion(mutation, before.Version);
            var now = clock.GetUtcNow();
            // No invented display name for preapproved identities; sign-in supplies the real one.
            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO admissions (provider, subject, state, display, requested_at, decided_at)
                VALUES (@provider, @subject, @state, '', @now, @now)
                ON DUPLICATE KEY UPDATE state = @state, decided_at = @now, revision = revision + 1
                """,
                ("@provider", identity.Provider), ("@subject", identity.Subject), ("@state", state), ("@now", now));

            await AuditAsync(connection, transaction, owner: null,
                $"admission.{state.ToString().ToLowerInvariant()}", identity.ToString(), mutation,
                new { before = before.State, after = state.ToString() });
            return await connection.ExistsAsync(transaction,
                "SELECT 1 FROM external_identities WHERE provider = @provider AND subject = @subject",
                ("@provider", identity.Provider), ("@subject", identity.Subject));
        }, ct);
        return new(hasAccount);
    }

    /// <summary>
    /// Null means no such account; a zero withdrawal count is a successful disablement. Account state,
    /// sessions, queued commands, and audit commit together so enabling cannot resurrect an old queue.
    /// </summary>
    public Task<AccountDisabledResult?> DisableAccountAsync(string userId, CancellationToken ct, AdminMutation? mutation = null)
        => db.InTransactionAsync<AccountDisabledResult?>(async (connection, transaction) =>
        {
            if (!await LockAccountAsync(connection, transaction, userId, mutation)) return null;

            var before = await connection.ReadOneAsync(transaction,
                "SELECT status FROM users WHERE id = @user", r => r.GetString(0), ("@user", userId));
            if (mutation is not null && before == "Disabled") return new AccountDisabledResult(0);
            await connection.ExecuteAsync(transaction,
                "UPDATE users SET status = 'Disabled' WHERE id = @user", ("@user", userId));
            await SessionStore.RevokeAllAsync(connection, transaction, userId, clock.GetUtcNow());

            // Same lock order as the host: account, computers, commands. Credentials stay valid
            // after re-enabling, but calls are refused while their owner is disabled.
            await connection.ExecuteAsync(transaction,
                "SELECT id FROM hosts WHERE owner_id = @user ORDER BY id FOR UPDATE", ("@user", userId));
            // An accepted command may already be executing locally; do not claim to stop it.
            var count = await connection.ExecuteAsync(transaction,
                "UPDATE commands SET status = @rejected WHERE owner_id = @user AND status = 'PendingDelivery'",
                ("@rejected", CommandStatus.Rejected), ("@user", userId));

            await AuditAsync(connection, transaction, userId, Audit.AccountDisabled, userId, mutation, new { before, after = "Disabled", withdrawnCommands = count });
            return new AccountDisabledResult(count);
        }, ct);

    public Task<bool> EnableAccountAsync(string userId, CancellationToken ct, AdminMutation? mutation = null)
        => db.InTransactionAsync<bool>(async (connection, transaction) =>
        {
            if (!await LockAccountAsync(connection, transaction, userId, mutation)) return false;
            // Enabling an already active account must not invalidate its current sessions.
            var before = await connection.ReadOneAsync(transaction,
                "SELECT status FROM users WHERE id = @user", r => r.GetString(0), ("@user", userId));
            if (mutation is not null && before == "Active") return true;
            // Earlier sessions stay revoked. The owner must sign in again after re-enabling.
            await connection.ExecuteAsync(transaction,
                "UPDATE users SET status = 'Active', security_version = security_version + @increment WHERE id = @user",
                ("@user", userId), ("@increment", mutation is null ? 0 : 1));
            await AuditAsync(connection, transaction, userId, Audit.AccountEnabled, userId, mutation, new { before, after = "Active" });
            return true;
        }, ct);

    public Task<bool> RevokeSessionsAsync(string userId, CancellationToken ct, AdminMutation? mutation = null)
        => db.InTransactionAsync<bool>(async (connection, transaction) =>
        {
            if (!await LockAccountAsync(connection, transaction, userId, mutation)) return false;
            await SessionStore.RevokeAllAsync(connection, transaction, userId, clock.GetUtcNow());
            await AuditAsync(connection, transaction, userId, Audit.SessionsRevoked, userId, mutation, new { revoked = true });
            return true;
        }, ct);

    private async Task<bool> LockAccountAsync(
        MySqlConnection connection, MySqlTransaction transaction, string userId, AdminMutation? mutation)
    {
        await AuthorizeAsync(connection, transaction, mutation);
        var row = await connection.ReadOneAsync(transaction,
            "SELECT security_version FROM users WHERE id = @user FOR UPDATE",
            r => (int?)r.GetInt32(0), ("@user", userId));
        if (row is null) return false;
        CheckVersion(mutation, row.Value);
        return true;
    }

    private static void CheckVersion(AdminMutation? mutation, int version)
    {
        // Never replay a stale revocation against sessions opened since the first request, or
        // silently overwrite another administrator's decision. The operator must refresh first.
        if (mutation is not null && mutation.ExpectedVersion != version)
            throw new GatewayFault("admin-conflict", 409, "The record changed. Refresh and review it before trying again.");
    }

    internal async Task AuthorizeAsync(MySqlConnection c, MySqlTransaction tx, AdminMutation? mutation)
    {
        if (mutation is null) return; // Trusted local CLI, with no browser identity.
        var session = mutation.Session;
        // Account then session, matching grant/revoke/logout. Holding both until commit closes
        // the race between HTTP authorization and a concurrent administrative access revocation.
        var validAdmin = await c.ExistsAsync(tx,
            "SELECT id FROM administrators WHERE id = @id AND enabled = 1 AND security_version = @version FOR UPDATE",
            ("@id", session.AdministratorId), ("@version", session.Version));
        var now = clock.GetUtcNow();
        var validSession = validAdmin && await c.ExistsAsync(tx, """
            SELECT id FROM administrator_sessions WHERE id = @id AND administrator_id = @admin
              AND security_version = @version AND revoked_at IS NULL AND expires_at > @now
              AND authenticated_at >= @fresh AND authenticated_at <= @future FOR UPDATE
            """, ("@id", session.SessionId), ("@admin", session.AdministratorId), ("@version", session.Version),
            ("@now", now), ("@fresh", now - AdminStore.FreshWindow), ("@future", now.AddSeconds(60)));
        if (!validSession) throw GatewayFault.Forbidden();
    }

    internal async Task AuditAsync(
        MySqlConnection connection, MySqlTransaction transaction, string? owner, string action, string target,
        AdminMutation? mutation = null, object? detail = null)
    {
        var actor = mutation is null ? Audit.Operator : "admin:" + mutation.Session.AdministratorId;
        await Audit.WriteAsync(connection, transaction, clock.GetUtcNow(), owner, actor, action, target);
        // Independent of ordinary-account deletion. A failed audit rolls back all access changes.
        await connection.ExecuteAsync(transaction, """
            INSERT INTO administrator_audit (at, actor, action, target, detail)
            VALUES (@at, @actor, @action, @target, @detail)
            """, ("@at", clock.GetUtcNow()), ("@actor", actor), ("@action", action), ("@target", target),
            ("@detail", detail is null ? null : JsonSerializer.Serialize(detail)));
    }
}
