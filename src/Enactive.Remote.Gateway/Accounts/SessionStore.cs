namespace Enactive.Remote.Gateway.Accounts;

using System.Globalization;
using System.Security.Claims;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MySqlConnector;

/// <summary>
/// A browser's sign-in, as a row: which person, under which security version, until when, and whether
/// it has been revoked.
///
/// <para>The cookie names a session and does not stand for one. It is a copy the browser keeps, and a
/// copy cannot be recalled: with only the cookie to go on, signing out, revoking every session and
/// disabling the account would all wait for the copy to expire. So every request is checked against
/// this row (<see cref="ValidAsync"/>), and ending a session is a write here.</para>
/// </summary>
public sealed class SessionStore(Database db, TimeProvider clock)
{
    /// <summary>
    /// Absolute, from sign-in: a session is not extended by being used. Eight hours, as the shared key's
    /// sessions were; how long a session should last is a separate decision from who it belongs to.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    /// <summary>
    /// A new session of an active account, signed in through <paramref name="provider"/>. The sign-in is
    /// written to the account's security log, as the provider and nothing else, in the same transaction:
    /// a session the person cannot see was opened is the one they would never think to end.
    /// </summary>
    public async Task<UserAccess> OpenAsync(string userId, string provider, CancellationToken ct)
        => (await OpenWithVersionAsync(userId, provider, ct)).Access;

    /// <summary>
    /// As <see cref="OpenAsync"/>, with the security version the session was opened under, which the
    /// cookie carries. Read and written in one transaction, so the version on the row is the account's
    /// version at the moment the row was made.
    /// </summary>
    internal async Task<(UserAccess Access, int SecurityVersion)> OpenWithVersionAsync(
        string userId, string provider, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        // Shared, so a disablement or a revoke-all committing now is ordered with this: it either comes
        // first and this sees it, or waits for this session to exist and then ends it too.
        var account = await connection.ReadOneAsync(transaction,
            "SELECT status, security_version FROM users WHERE id = @id FOR SHARE",
            reader => (Status: reader.GetString("status"), Version: reader.GetInt32("security_version")),
            ("@id", userId));

        if (account.Status is null)
        {
            throw new InvalidOperationException($"Account {userId} does not exist, so it cannot sign in.");
        }

        if (account.Status != "Active")
        {
            throw GatewayFault.AccountDisabledForPerson();
        }

        var sessionId = Ids.New();
        var now = clock.GetUtcNow();

        await connection.ExecuteAsync(transaction,
            """
            INSERT INTO user_sessions (id, user_id, security_version, created_at, expires_at)
            VALUES (@id, @user, @version, @now, @expires)
            """,
            ("@id", sessionId), ("@user", userId), ("@version", account.Version), ("@now", now),
            ("@expires", now.Add(Lifetime)));

        await Audit.WriteAsync(connection, transaction, now, userId, Audit.User(userId), Audit.SignIn(provider), null);

        await transaction.CommitAsync(ct);
        return (new UserAccess(userId, sessionId), account.Version);
    }

    /// <summary>
    /// True only when the account is active, and the session exists, is this account's, is unexpired and
    /// unrevoked, and was opened under the account's current security version. One query, on the
    /// session's primary key and the account's.
    /// </summary>
    public async Task<bool> ValidAsync(
        string userId, string sessionId, int securityVersion, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        return await connection.ExistsAsync(null,
            """
            SELECT 1
            FROM user_sessions s JOIN users u ON u.id = s.user_id
            WHERE s.id = @session AND s.user_id = @user
              AND s.revoked_at IS NULL AND s.expires_at > @now
              AND s.security_version = @version AND u.security_version = @version
              AND u.status = 'Active'
            """,
            ("@session", sessionId), ("@user", userId), ("@now", clock.GetUtcNow()),
            ("@version", securityVersion));
    }

    /// <summary>
    /// Whether this browser's session was opened - signed in - within the last <paramref name="window"/>, and
    /// still stands. Measured from the opening and not from the last use: a session a cookie left behind keeps
    /// being used by whoever holds it, and use proves nothing about who is at the keyboard now.
    /// </summary>
    public async Task<bool> OpenedWithinAsync(UserAccess s, TimeSpan window, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        return await connection.ExistsAsync(null,
            """
            SELECT 1 FROM user_sessions
            WHERE id = @session AND user_id = @user AND revoked_at IS NULL AND created_at > @since
            """,
            ("@session", s.SessionId), ("@user", s.UserId), ("@since", clock.GetUtcNow() - window));
    }

    /// <summary>Ends one session: the one this browser is signed in with.</summary>
    public async Task RevokeAsync(UserAccess s, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(null,
            """
            UPDATE user_sessions SET revoked_at = @now
            WHERE id = @session AND user_id = @user AND revoked_at IS NULL
            """,
            ("@now", clock.GetUtcNow()), ("@session", s.SessionId), ("@user", s.UserId));
    }

    /// <summary>
    /// The person signing themselves out everywhere: ends every session of the account, and moves its
    /// security version on. The version is what makes this complete: a session opened by a sign-in racing
    /// this one is stamped with the old version whichever way the race goes, and no longer matches. Written
    /// to the account's security log as the person's own act; the operator's revocation is written as the
    /// operator's (<see cref="AdminCommands"/>).
    /// </summary>
    public async Task RevokeAllAsync(string userId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);
        var now = clock.GetUtcNow();

        await RevokeAllAsync(connection, transaction, userId, now);
        await Audit.WriteAsync(
            connection, transaction, now, userId, Audit.User(userId), Audit.SignOutEverywhere, null);

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// <see cref="RevokeAllAsync(string, CancellationToken)"/> inside a transaction somebody else owns.
    /// Disabling an account has to end its sessions in the same transaction that sets its status: done
    /// as a second step, a failure between the two left a disabled account whose sessions were still
    /// being refused only by the status check, and an account enabled again would have woken them up.
    /// </summary>
    internal static async Task RevokeAllAsync(
        MySqlConnection connection, MySqlTransaction transaction, string userId, DateTimeOffset now)
    {
        // The account first, which is the order a sign-in takes them in.
        await connection.ExecuteAsync(transaction,
            "UPDATE users SET security_version = security_version + 1 WHERE id = @user",
            ("@user", userId));

        await connection.ExecuteAsync(transaction,
            "UPDATE user_sessions SET revoked_at = @now WHERE user_id = @user AND revoked_at IS NULL",
            ("@now", now), ("@user", userId));
    }
}

/// <summary>The <c>User</c> cookie scheme: what it carries, how it is issued, and how it is checked.</summary>
public static class UserCookie
{
    public const string SchemeName = "User";

    public const string CookieName = "Enactive.User";

    // The claims. Short because they travel in every request's cookie, and nothing but this class
    // reads them.
    private const string UserClaim = "uid";
    private const string SessionClaim = "sid";
    private const string VersionClaim = "sv";

    /// <summary>
    /// Opens a session for <paramref name="userId"/>, signed in through <paramref name="provider"/>, and
    /// gives this browser its cookie.
    /// </summary>
    public static async Task SignInAsync(
        HttpContext context, SessionStore sessions, string userId, string provider, CancellationToken ct)
    {
        var (access, version) = await sessions.OpenWithVersionAsync(userId, provider, ct);
        await IssueAsync(context, access, version);
    }

    /// <summary>
    /// Gives this browser the cookie of a session already opened, under the security version it was
    /// opened with. For a sign-in whose session was made by <see cref="AccountService.SignInAsync"/>.
    /// </summary>
    public static async Task IssueAsync(HttpContext context, UserAccess access, int version)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(UserClaim, access.UserId),
                new Claim(SessionClaim, access.SessionId),
                new Claim(VersionClaim, version.ToString(CultureInfo.InvariantCulture))
            ],
            SchemeName);

        await context.SignInAsync(SchemeName, new ClaimsPrincipal(identity));
    }

    /// <summary>
    /// The per-request check (<see cref="SessionStore.ValidAsync"/>). A cookie whose session no longer
    /// stands is rejected for this request and deleted, so the browser stops sending it.
    /// </summary>
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var userId = principal?.FindFirstValue(UserClaim);
        var sessionId = principal?.FindFirstValue(SessionClaim);
        var versionText = principal?.FindFirstValue(VersionClaim);

        var valid = userId is not null && sessionId is not null
            && int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            && await context.HttpContext.RequestServices.GetRequiredService<SessionStore>()
                .ValidAsync(userId, sessionId, version, context.HttpContext.RequestAborted);

        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(SchemeName);
        }
    }

    /// <summary>
    /// The signed-in person's id, or null when this request's cookie names nobody. For an anonymous
    /// endpoint that treats the two differently; anything acting for the person uses
    /// <see cref="UserAccess"/>, which refuses rather than returning null.
    /// </summary>
    public static string? SignedInUserId(this HttpContext context) => context.User.FindFirstValue(UserClaim);

    /// <summary>
    /// The signed-in person, from this request's cookie. The <c>/api</c> group's authorization lets no
    /// request without one through, so the refusal here is a backstop: a handler mapped outside the
    /// group by mistake fails closed instead of acting for an empty id.
    /// </summary>
    public static UserAccess UserAccess(this HttpContext context)
    {
        var userId = context.User.FindFirstValue(UserClaim);
        var sessionId = context.User.FindFirstValue(SessionClaim);

        return userId is null || sessionId is null
            ? throw GatewayFault.Unauthenticated()
            : new UserAccess(userId, sessionId);
    }
}
