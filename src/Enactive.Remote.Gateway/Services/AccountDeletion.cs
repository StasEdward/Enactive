namespace Enactive.Remote.Gateway.Services;

using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>
/// A person deleting their own account, and everything stored for it.
///
/// <para><b>One transaction.</b> The account's row is deleted, and the foreign keys cascade from it to every
/// table that names an owner - identities, sessions, devices, computers and all they hold, the security log.
/// Rows deleted one table at a time would leave, after a failure half-way, an account that is partly gone:
/// a person told nothing is left while their tasks are still stored.</para>
///
/// <para><b>What the cascade does not reach.</b> An admission names an identity (provider and subject), not an
/// account - it exists before there is one - so no foreign key leads to it, and the operator's row recording
/// the decision belongs to nobody and names the identity in its target. Both are deleted here, in the same
/// transaction, for every identity linked to the account: left, they kept a record that this person was
/// here after the person was told everything of theirs was gone. A deleted person who signs in again is
/// therefore a stranger asking for admission, as before their first account.</para>
///
/// <para><b>After the commit.</b> The account's computers are cut off (their connections closed: a call on one
/// still open would go on being served until it next touched its rows), and the operator's log says that an
/// account was deleted - and nothing about whose, which is what the deletion was asked for.</para>
/// </summary>
public sealed class AccountDeletion(
    Database db, SessionStore sessions, HostConnections connections, ILogger<AccountDeletion> log)
{
    /// <summary>
    /// How recently the session must have been opened. Deleting cannot be undone, and a session lasts eight
    /// hours: a cookie left signed in on a borrowed computer, or taken from one, would otherwise be enough to
    /// end everything. Signing in again is what shows the person is at the keyboard now.
    /// </summary>
    public static readonly TimeSpan RecentSignIn = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Deletes the account <paramref name="user"/> is signed in to, or refuses with <c>reauthenticate</c> when
    /// the session was opened longer ago than <see cref="RecentSignIn"/>.
    /// </summary>
    public async Task DeleteAsync(UserAccess user, CancellationToken ct)
    {
        if (!await sessions.OpenedWithinAsync(user, RecentSignIn, ct))
        {
            throw GatewayFault.Reauthenticate();
        }

        var computers = await db.InTransactionAsync<IReadOnlyList<string>>(async (connection, transaction) =>
        {
            // The account first, the order every path takes: a computer's call in flight holds it shared and
            // is waited for, and one arriving now waits for this and then finds no account.
            await Quota.LockAccountAsync(connection, transaction, user.UserId);

            // Read before the delete, which takes them with it; closed after the commit.
            var hosts = await connection.ReadAllAsync(transaction,
                "SELECT id FROM hosts WHERE owner_id = @user ORDER BY id",
                reader => reader.GetString("id"), ("@user", user.UserId));

            var identities = await connection.ReadAllAsync(transaction,
                "SELECT provider, subject FROM external_identities WHERE user_id = @user ORDER BY provider, subject",
                reader => (Provider: reader.GetString("provider"), Subject: reader.GetString("subject")),
                ("@user", user.UserId));

            foreach (var (provider, subject) in identities)
            {
                await ForgetIdentityAsync(connection, transaction, provider, subject);
            }

            await connection.ExecuteAsync(transaction, "DELETE FROM users WHERE id = @user", ("@user", user.UserId));
            return hosts;
        }, ct);

        foreach (var hostId in computers)
        {
            connections.CloseAll(hostId);
        }

        log.LogInformation("An account was deleted.");
    }

    /// <summary>
    /// The admission of one identity, and the operator's rows about it: those with no owner whose target is
    /// the identity as the operator's command line writes it, <c>provider:subject</c>, cut as a row stores it.
    /// Rows that have an owner are the account's own and go with it in the cascade.
    /// </summary>
    private static async Task ForgetIdentityAsync(
        MySqlConnection connection, MySqlTransaction transaction, string provider, string subject)
    {
        await connection.ExecuteAsync(transaction,
            "DELETE FROM admissions WHERE provider = @provider AND subject = @subject",
            ("@provider", provider), ("@subject", subject));

        await connection.ExecuteAsync(transaction,
            "DELETE FROM audit WHERE owner_id IS NULL AND target = @target",
            ("@target", Audit.Cut($"{provider}:{subject}")));
    }
}
