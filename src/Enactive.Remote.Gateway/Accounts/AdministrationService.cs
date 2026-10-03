namespace Enactive.Remote.Gateway.Accounts;

using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

internal sealed record WaitingAdmission(AdmissionIdentity Identity, string Display, DateTimeOffset RequestedAt);
internal sealed record AdmissionDecisionResult(bool HasAccount);
internal sealed record AccountDisabledResult(int WithdrawnCommands);

/// <summary>
/// Administrative operations shared by host adapters. They return data, never terminal output, so
/// a web adapter does not have to repeat the SQL or interpret the CLI's prose. This is a trusted
/// server-side service, not authorization: an HTTP adapter must authenticate the administrator first.
/// No endpoints expose it until the separate administrative authentication boundary is implemented.
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
        AdmissionIdentity identity, AdmissionState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        // The CLI used to limit this through its command switch. Keep the invariant here as well,
        // or a new adapter could persist Waiting or an undefined enum as an operator's decision.
        if (state is not (AdmissionState.Approved or AdmissionState.Refused))
            throw new ArgumentOutOfRangeException(nameof(state), "An admission decision must approve or refuse.");

        var hasAccount = await db.InTransactionAsync<bool>(async (connection, transaction) =>
        {
            var now = clock.GetUtcNow();
            // No invented display name for preapproved identities; sign-in supplies the real one.
            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO admissions (provider, subject, state, display, requested_at, decided_at)
                VALUES (@provider, @subject, @state, '', @now, @now)
                ON DUPLICATE KEY UPDATE state = @state, decided_at = @now
                """,
                ("@provider", identity.Provider), ("@subject", identity.Subject), ("@state", state), ("@now", now));

            await AuditAsync(connection, transaction, owner: null,
                $"admission.{state.ToString().ToLowerInvariant()}", identity.ToString());
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
    public Task<AccountDisabledResult?> DisableAccountAsync(string userId, CancellationToken ct)
        => db.InTransactionAsync<AccountDisabledResult?>(async (connection, transaction) =>
        {
            if (!await LockAccountAsync(connection, transaction, userId)) return null;

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

            await AuditAsync(connection, transaction, userId, Audit.AccountDisabled, userId);
            return new AccountDisabledResult(count);
        }, ct);

    public Task<bool> EnableAccountAsync(string userId, CancellationToken ct)
        => db.InTransactionAsync<bool>(async (connection, transaction) =>
        {
            if (!await LockAccountAsync(connection, transaction, userId)) return false;
            // Earlier sessions stay revoked. The owner must sign in again after re-enabling.
            await connection.ExecuteAsync(transaction,
                "UPDATE users SET status = 'Active' WHERE id = @user", ("@user", userId));
            await AuditAsync(connection, transaction, userId, Audit.AccountEnabled, userId);
            return true;
        }, ct);

    public Task<bool> RevokeSessionsAsync(string userId, CancellationToken ct)
        => db.InTransactionAsync<bool>(async (connection, transaction) =>
        {
            if (!await LockAccountAsync(connection, transaction, userId)) return false;
            await SessionStore.RevokeAllAsync(connection, transaction, userId, clock.GetUtcNow());
            await AuditAsync(connection, transaction, userId, Audit.SessionsRevoked, userId);
            return true;
        }, ct);

    private static Task<bool> LockAccountAsync(
        MySqlConnection connection, MySqlTransaction transaction, string userId)
        => connection.ExistsAsync(transaction,
            "SELECT 1 FROM users WHERE id = @user FOR UPDATE", ("@user", userId));

    private Task AuditAsync(
        MySqlConnection connection, MySqlTransaction transaction, string? owner, string action, string target)
        => Audit.WriteAsync(connection, transaction, clock.GetUtcNow(), owner, Audit.Operator, action, target);
}
