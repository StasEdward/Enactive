namespace Enactive.Remote.Gateway.Services;

using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>
/// The security log: one row for each thing done to a person's access - a sign-in, a sign-out everywhere,
/// a device or a computer added or removed, an invitation made, the operator stopping or reopening the
/// account. Each row is written in the transaction that made the change, so there is no change without its
/// row and no row for a change that rolled back.
///
/// <para><b>What a row holds.</b> Who (<c>user:&lt;id&gt;</c>, <c>host:&lt;id&gt;</c> or <c>operator</c>),
/// what (an action below), to which id, and when - never an address, a key, a label, a provider's subject
/// or an envelope. The person reads their own rows (<see cref="ReadAsync"/>), and the operator's admissions
/// of identities with no account yet belong to nobody.</para>
///
/// <para><b>Names.</b> Every action is <c>noun.verb</c>. The first rows were written in two styles,
/// <c>device-registered</c> beside <c>account.disabled</c>, and a reader matching one style missed the
/// other's rows.</para>
/// </summary>
public static class Audit
{
    public const string DeviceRegistered = "device.registered";
    public const string DeviceEnrolled = "device.enrolled";
    public const string DeviceRevoked = "device.revoked";
    public const string InviteCreated = "invite.created";
    public const string HostRegistered = "host.registered";
    public const string HostRevoked = "host.revoked";
    public const string SignOutEverywhere = "signout.everywhere";
    public const string AccountDisabled = "account.disabled";
    public const string AccountEnabled = "account.enabled";
    public const string SessionsRevoked = "sessions.revoked";

    /// <summary>The operator, at the gateway's command line.</summary>
    public const string Operator = "operator";

    /// <summary>
    /// How many rows the person is shown, the newest. The trail is kept for months, and a busy account's whole
    /// trail in one answer would make every open of the log cost more than the last.
    /// </summary>
    public const int Newest = 200;

    // The width of audit.target. A longer target is cut in the row only: an insert past the column's width
    // would fail, and the change it records with it: an operator's approval of an identity longer than the
    // column would never go through.
    private const int TargetWidth = 100;

    /// <summary>A sign-in, as the provider it came through: <c>signin.github</c>. Never the subject.</summary>
    public static string SignIn(string provider) => "signin." + provider;

    /// <summary>The person, acting for themselves.</summary>
    public static string User(string userId) => "user:" + userId;

    /// <summary>One of the person's computers, acting on its own credential.</summary>
    public static string Host(string hostId) => "host:" + hostId;

    /// <summary>
    /// Writes one row, in <paramref name="transaction"/>. <paramref name="ownerId"/> is the account whose log
    /// it joins, or null for a row that is nobody's yet (an admission).
    /// </summary>
    public static Task WriteAsync(
        MySqlConnection connection, MySqlTransaction transaction, DateTimeOffset at, string? ownerId,
        string actor, string action, string? target)
        => connection.ExecuteAsync(transaction,
            "INSERT INTO audit (owner_id, at, actor, action, target) VALUES (@owner, @at, @actor, @action, @target)",
            ("@owner", (object?)ownerId ?? DBNull.Value), ("@at", at), ("@actor", actor), ("@action", action),
            ("@target", target is null ? DBNull.Value : Cut(target)));

    /// <summary>
    /// <paramref name="target"/> as a row stores it, cut to the column's width. Also what a row is looked for
    /// by: deleting an account removes the operator's rows naming its identities, and an identity longer than
    /// the column is in those rows only as far as the cut.
    /// </summary>
    internal static string Cut(string target) => target.Length <= TargetWidth ? target : target[..TargetWidth];

    /// <summary>
    /// The person's own rows, the newest first and at most <see cref="Newest"/> of them. Only rows whose owner
    /// is the caller's account: another person's rows, and rows that are nobody's, are not in what this reads.
    /// </summary>
    public static async Task<IReadOnlyList<AuditEntry>> ReadAsync(Database db, UserAccess user, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        // By the time and then the id: rows written in the same millisecond come back in the order they
        // were written, newest first, rather than in whatever order the index happens to give.
        return await connection.ReadAllAsync(null,
            """
            SELECT at, actor, action, target FROM audit
            WHERE owner_id = @owner
            ORDER BY at DESC, id DESC
            LIMIT @newest
            """,
            reader => new AuditEntry(
                reader.Utc("at"), reader.GetString("actor"), reader.GetString("action"), reader.StringOrNull("target")),
            ("@owner", user.UserId), ("@newest", Newest));
    }
}

/// <summary>One row of the security log as the person reads it.</summary>
public sealed record AuditEntry(DateTimeOffset At, string Actor, string Action, string? Target);
