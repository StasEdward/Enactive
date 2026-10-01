namespace Enactive.Remote.Gateway.Accounts;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>
/// The operator's command line: <c>dotnet Enactive.Remote.Gateway.dll admin &lt;command&gt;</c>, run on
/// the gateway's machine against the same database. It is how people are admitted and how an account is
/// ended, and it needs nothing but the connection string - not the web application, not the providers.
///
/// <para>Every change is written to <c>audit</c> as actor <c>operator</c>, in the same transaction as the
/// change: a change nobody can later see was made is as bad as one that was not recorded at all.</para>
///
/// <para>Exit codes: 0 done, 1 the request was understood and cannot be carried out (no such account),
/// 2 not understood.</para>
/// </summary>
public static partial class AdminCommands
{
    public const string Actor = "operator";

    // The width of audit.target. A longer identity is cut in the audit row only: the admissions row holds
    // the whole of it and is what the decision is made on, but an insert past the column's width would
    // fail and the operator's approval with it.
    private const int AuditTargetWidth = 100;

    private const string Usage =
        """
        Usage: admin <command>
          admissions                        list the identities waiting for approval
          approve <provider>:<subject>      let this identity create an account (e.g. github:12345)
          refuse <provider>:<subject>       turn this identity away
          disable <userId>                  stop an account: sign-ins, sessions and queued commands
          enable <userId>                   let a disabled account sign in again
          sessions revoke <userId>          sign the account out everywhere
        """;

    /// <param name="args">The command and its arguments, without the leading <c>admin</c>.</param>
    /// <param name="clock">For tests; the system clock otherwise.</param>
    public static async Task<int> RunAsync(
        string[] args, Database db, TextWriter output, TimeProvider? clock = null, CancellationToken ct = default)
    {
        clock ??= TimeProvider.System;

        switch (args)
        {
            case ["admissions"]:
                return await ListWaitingAsync(db, output, ct);

            case ["approve", var identity] when Identity.TryParse(identity, out var who):
                return await DecideAsync(db, output, clock, who, AdmissionState.Approved, ct);

            case ["refuse", var identity] when Identity.TryParse(identity, out var who):
                return await DecideAsync(db, output, clock, who, AdmissionState.Refused, ct);

            case ["disable", var userId]:
                return await DisableAsync(db, output, clock, userId, ct);

            case ["enable", var userId]:
                return await EnableAsync(db, output, clock, userId, ct);

            case ["sessions", "revoke", var userId]:
                return await RevokeSessionsAsync(db, output, clock, userId, ct);

            default:
                await output.WriteLineAsync(Usage);
                return 2;
        }
    }

    private static async Task<int> ListWaitingAsync(Database db, TextWriter output, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var waiting = await connection.ReadAllAsync(null,
            """
            SELECT provider, subject, display, requested_at FROM admissions
            WHERE state = 'Waiting' ORDER BY requested_at, provider, subject
            """,
            reader => (
                Identity: $"{reader.GetString("provider")}:{reader.GetString("subject")}",
                Display: reader.GetString("display"),
                At: reader.Utc("requested_at")));

        if (waiting.Count == 0)
        {
            await output.WriteLineAsync("No identities are waiting.");
            return 0;
        }

        foreach (var (identity, display, at) in waiting)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{identity}  {Printable(display)}  asked {at:yyyy-MM-dd HH:mm:ss}Z"));
        }

        return 0;
    }

    /// <summary>
    /// Records the decision, making the row when the identity has not asked yet: an operator may admit
    /// somebody before they try. The decision moves the row whatever state it was in.
    /// </summary>
    private static async Task<int> DecideAsync(
        Database db, TextWriter output, TimeProvider clock, Identity who, AdmissionState state,
        CancellationToken ct)
    {
        var hasAccount = await db.InTransactionAsync<bool>(async (connection, transaction) =>
        {
            var now = clock.GetUtcNow();

            // An empty display for a row made here: the person's own sign-in fills it in.
            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO admissions (provider, subject, state, display, requested_at, decided_at)
                VALUES (@provider, @subject, @state, '', @now, @now)
                ON DUPLICATE KEY UPDATE state = @state, decided_at = @now
                """,
                ("@provider", who.Provider), ("@subject", who.Subject), ("@state", state), ("@now", now));

            await AuditAsync(connection, transaction, clock, owner: null,
                $"admission.{state.ToString().ToLowerInvariant()}", who.ToString());

            return await connection.ExistsAsync(transaction,
                "SELECT 1 FROM external_identities WHERE provider = @provider AND subject = @subject",
                ("@provider", who.Provider), ("@subject", who.Subject));
        }, ct);

        await output.WriteLineAsync($"{who} is now {state.ToString().ToLowerInvariant()}.");

        // The list decides who may CREATE an account. Saying so here saves the operator a refusal that
        // changes nothing and looks as if it had.
        if (hasAccount && state == AdmissionState.Refused)
        {
            await output.WriteLineAsync(
                "Note: this identity already has an account, and refusing does not stop it signing in. "
                + "Use disable <userId> for that.");
        }

        return 0;
    }

    /// <summary>
    /// Stops an account, all in one transaction: the status, every session, and the commands queued for
    /// its computers. Apart, a failure part-way left an account that was disabled but whose queue was
    /// still full - and enabling it again would have handed its computers instructions the person gave
    /// before they were stopped.
    ///
    /// <para>The account is locked first, then its computers, then their commands: the order the
    /// Host's own calls take them in, so this waits for a call in flight rather than crossing it. The
    /// computers' credentials are not revoked; they are refused for as long as the owner is not active,
    /// at their next call, and work again if the account is enabled.</para>
    ///
    /// <para>Only commands nobody has accepted are withdrawn, as when one computer is revoked: a Host
    /// owns what it has accepted and may be carrying it out.</para>
    /// </summary>
    private static async Task<int> DisableAsync(
        Database db, TextWriter output, TimeProvider clock, string userId, CancellationToken ct)
    {
        var withdrawn = await db.InTransactionAsync<int?>(async (connection, transaction) =>
        {
            if (!await LockAccountAsync(connection, transaction, userId))
            {
                return null;
            }

            await connection.ExecuteAsync(transaction,
                "UPDATE users SET status = 'Disabled' WHERE id = @user", ("@user", userId));
            await SessionStore.RevokeAllAsync(connection, transaction, userId, clock.GetUtcNow());

            await connection.ExecuteAsync(transaction,
                "SELECT id FROM hosts WHERE owner_id = @user ORDER BY id FOR UPDATE", ("@user", userId));

            var count = await connection.ExecuteAsync(transaction,
                "UPDATE commands SET status = @rejected WHERE owner_id = @user AND status = 'PendingDelivery'",
                ("@rejected", CommandStatus.Rejected), ("@user", userId));

            await AuditAsync(connection, transaction, clock, userId, "account.disabled", userId);
            return count;
        }, ct);

        if (withdrawn is null)
        {
            return await NoSuchAccountAsync(output, userId);
        }

        await output.WriteLineAsync(
            $"Disabled {userId}: sessions ended, {withdrawn} undelivered command(s) withdrawn.");
        return 0;
    }

    private static async Task<int> EnableAsync(
        Database db, TextWriter output, TimeProvider clock, string userId, CancellationToken ct)
    {
        var found = await db.InTransactionAsync<bool>(async (connection, transaction) =>
        {
            if (!await LockAccountAsync(connection, transaction, userId))
            {
                return false;
            }

            await connection.ExecuteAsync(transaction,
                "UPDATE users SET status = 'Active' WHERE id = @user", ("@user", userId));
            await AuditAsync(connection, transaction, clock, userId, "account.enabled", userId);
            return true;
        }, ct);

        if (!found)
        {
            return await NoSuchAccountAsync(output, userId);
        }

        await output.WriteLineAsync($"Enabled {userId}. Earlier sessions stay ended; the person signs in again.");
        return 0;
    }

    private static async Task<int> RevokeSessionsAsync(
        Database db, TextWriter output, TimeProvider clock, string userId, CancellationToken ct)
    {
        var found = await db.InTransactionAsync<bool>(async (connection, transaction) =>
        {
            if (!await LockAccountAsync(connection, transaction, userId))
            {
                return false;
            }

            await SessionStore.RevokeAllAsync(connection, transaction, userId, clock.GetUtcNow());
            await AuditAsync(connection, transaction, clock, userId, "sessions.revoked", userId);
            return true;
        }, ct);

        if (!found)
        {
            return await NoSuchAccountAsync(output, userId);
        }

        await output.WriteLineAsync($"Every session of {userId} has ended.");
        return 0;
    }

    private static async Task<int> NoSuchAccountAsync(TextWriter output, string userId)
    {
        await output.WriteLineAsync($"There is no account with id {Printable(userId)}.");
        return 1;
    }

    /// <summary>Locks the account's row, which is how every path orders itself against it.</summary>
    private static Task<bool> LockAccountAsync(
        MySqlConnection connection, MySqlTransaction transaction, string userId)
        => connection.ExistsAsync(transaction,
            "SELECT 1 FROM users WHERE id = @user FOR UPDATE", ("@user", userId));

    private static Task AuditAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        TimeProvider clock, string? owner, string action, string target)
        => connection.ExecuteAsync(transaction,
            "INSERT INTO audit (owner_id, at, actor, action, target) VALUES (@owner, @at, @actor, @action, @target)",
            ("@owner", (object?)owner ?? DBNull.Value), ("@at", clock.GetUtcNow()), ("@actor", Actor),
            ("@action", action), ("@target", target.Length <= AuditTargetWidth ? target : target[..AuditTargetWidth]));

    /// <summary>
    /// Text a stranger chose, made safe for the operator's terminal. A display name is whatever the
    /// provider reports, and one carrying an escape sequence could clear the screen or draw a line that
    /// looks like another identity's - so control characters are shown as question marks.
    /// </summary>
    private static string Printable(string text)
    {
        var shown = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            shown.Append(char.IsControl(character) ? '?' : character);
        }

        return shown.ToString();
    }

    /// <summary>
    /// <c>provider:subject</c>. Both are stored in ASCII columns, so anything else would be stored with
    /// question marks - two different identities collapsing into one row - and is refused here instead.
    /// The provider is lowercase and short, as its column is; the subject is whatever the provider's
    /// stable id looks like, printable and without spaces. The pattern ends at <c>\z</c> and not at
    /// <c>$</c>, which also matches before a trailing newline: that would admit "github:12345\n", a
    /// subject no provider ever reports, as a different row from the one the sign-in looks for.
    /// </summary>
    private readonly partial record struct Identity(string Provider, string Subject)
    {
        public static bool TryParse(string text, out Identity identity)
        {
            var match = Pattern().Match(text);
            identity = match.Success ? new Identity(match.Groups[1].Value, match.Groups[2].Value) : default;
            return match.Success;
        }

        public override string ToString() => $"{Provider}:{Subject}";

        [GeneratedRegex(@"^([a-z0-9_-]{1,20}):([\x21-\x7E]{1,255})\z")]
        private static partial Regex Pattern();
    }
}
