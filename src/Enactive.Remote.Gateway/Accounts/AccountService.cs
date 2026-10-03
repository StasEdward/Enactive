namespace Enactive.Remote.Gateway.Accounts;

using System.Globalization;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>Who may create an account: the operator's list, or anybody.</summary>
public enum AdmissionMode
{
    /// <summary>Only an identity the operator has approved. The default.</summary>
    List,

    /// <summary>Every identity not turned away, on first sign-in. Needs real limits first.</summary>
    Open
}

/// <summary>The admission setting and what must hold before it may be switched to open.</summary>
public static class Admission
{
    public const string Setting = "ENACTIVE_ADMISSION";

    /// <summary>
    /// The configured mode; "list" when nothing is configured. Anything else is refused: a typo in the
    /// switch that decides who may have an account must not be read as either answer.
    /// </summary>
    public static AdmissionMode FromConfiguration(IConfiguration configuration)
    {
        var value = configuration[Setting];

        if (string.IsNullOrWhiteSpace(value))
        {
            return AdmissionMode.List;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "list" => AdmissionMode.List,
            "open" => AdmissionMode.Open,
            _ => throw new InvalidOperationException($"{Setting} is '{value}'; it must be list or open.")
        };
    }

    /// <summary>
    /// Refuses open admission while nothing limits what an account may use. Open means strangers; with
    /// no ceilings one stranger's runaway script fills the database and the queue for everybody, and
    /// nothing about the running gateway would look wrong until it did.
    ///
    /// <para>Compared by value, so a limits object that merely equals <see cref="Limits.Unlimited"/>
    /// counts as having none. The gateway reads its limits from configuration, which never gives these;
    /// this keeps a host that registers the unlimited ones - a test, a tool built on the gateway - from
    /// admitting the world with them.</para>
    /// </summary>
    public static void RequireLimits(AdmissionMode mode, Limits limits)
    {
        if (mode == AdmissionMode.Open && limits == Limits.Unlimited)
        {
            throw new InvalidOperationException(
                $"{Setting} is open, and the gateway has no per-account limits: every stranger who "
                + "signed in could use as much as they liked. Set real limits first, or use list.");
        }
    }
}

/// <summary>An admission row's state: the operator's decision about an identity.</summary>
internal enum AdmissionState
{
    Waiting,
    Approved,
    Refused
}

/// <summary>What signing in came to. A closed set: the caller handles each, and none is an exception.</summary>
public abstract record SignInOutcome
{
    private SignInOutcome()
    {
    }

    /// <summary>
    /// Signed in, with the session just opened. The security version is what the session was opened
    /// under and the cookie must carry it, or the first request would find it stale.
    /// </summary>
    public sealed record SignedIn(UserAccess Access, int SecurityVersion) : SignInOutcome;

    /// <summary>Not admitted yet; the identity is on the operator's list. No account, no session.</summary>
    public sealed record Waiting : SignInOutcome;

    /// <summary>The operator turned this identity away. No account, no session.</summary>
    public sealed record Refused : SignInOutcome;

    /// <summary>The account exists and has been disabled. No session.</summary>
    public sealed record Disabled : SignInOutcome;

    /// <summary>
    /// The provider's answer was redeemed already, or was issued before the account's sessions were last
    /// ended. No session: either is what a kept copy of somebody's answer looks like.
    /// </summary>
    public sealed record Spent : SignInOutcome;
}

/// <summary>
/// The provider's answer being redeemed: the random id it was issued with (64 hex characters) and when it was
/// issued. What makes it good for one sign-in, and for none after the account signed out everywhere.
/// </summary>
public sealed record SignInTicket(string Id, DateTimeOffset Issued);

/// <summary>People of the gateway: how an account comes to exist.</summary>
public sealed class AccountService(Database db, TimeProvider clock, AdmissionMode admission = AdmissionMode.List)
{
    private readonly SessionStore _sessions = new(db, clock);

    // The widths of users.display_name and external_identities.display. A longer name is cut here,
    // because the database would otherwise refuse the sign-in of anyone whose provider hands back a
    // long display name - the person could never get an account at all.
    private const int DisplayNameWidth = 100;
    private const int IdentityDisplayWidth = 200;

    /// <summary>
    /// Signs an identity in. A person who already has an account signs in whatever the admission list
    /// now says - they were admitted once, and ending an account is what disabling is for. Anyone else
    /// is admitted only if the list (or open admission) says so, and is otherwise recorded as waiting
    /// for the operator to see.
    ///
    /// <para>The identity is the provider and its subject, the numeric GitHub id or the Google
    /// <c>sub</c>. The display name is only what the person is called: it is kept up to date when the
    /// provider reports a new one and is never what finds the account, and an email never does.</para>
    ///
    /// <para>The <paramref name="ticket"/> is redeemed with the session it opens, and only then: an answer that
    /// ended waiting or refused opened nothing, and saying so again is all a second redemption of it can do
    /// (<see cref="SessionStore.RedeemAsync"/>).</para>
    /// </summary>
    public async Task<SignInOutcome> SignInAsync(
        string provider, string subject, string display, SignInTicket ticket, CancellationToken ct)
    {
        var userId = await IdentityUserAsync(provider, subject, display, ct);

        if (userId is null)
        {
            switch (await AdmitAsync(provider, subject, display, ct))
            {
                case AdmissionState.Waiting:
                    return new SignInOutcome.Waiting();
                case AdmissionState.Refused:
                    return new SignInOutcome.Refused();
            }

            userId = await ProvisionWithoutAdmissionAsync(provider, subject, display, ct);
        }

        try
        {
            return await _sessions.RedeemAsync(userId, provider, ticket, ct) is { } opened
                ? new SignInOutcome.SignedIn(opened.Access, opened.SecurityVersion)
                : new SignInOutcome.Spent();
        }
        catch (GatewayFault fault) when (fault.Code == FaultCode.AccountDisabled)
        {
            // The status is read where the session is made, under a lock, so a disablement that
            // commits between this person being found and their session opening is still seen.
            return new SignInOutcome.Disabled();
        }
    }

    /// <summary>
    /// The account this identity already has, if any. Refreshes the name it is shown under, only when it
    /// changed: a sign-in that wrote on every call would lock the row for nothing.
    /// </summary>
    private async Task<string?> IdentityUserAsync(
        string provider, string subject, string display, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        var userId = await connection.ReadOneAsync(null,
            "SELECT user_id FROM external_identities WHERE provider = @provider AND subject = @subject",
            reader => reader.GetString("user_id"), ("@provider", provider), ("@subject", subject));

        if (userId is not null)
        {
            await connection.ExecuteAsync(null,
                """
                UPDATE external_identities SET display = @display
                WHERE provider = @provider AND subject = @subject AND display <> @display
                """,
                ("@display", Truncate(display, IdentityDisplayWidth)),
                ("@provider", provider), ("@subject", subject));
        }

        return userId;
    }

    /// <summary>
    /// What the admission list says about an identity with no account, recording that it asked. The
    /// row is made on the first ask, as Waiting or - under open admission - Approved, and its decision
    /// is never overwritten by asking again: only the operator moves it. The one exception is a row
    /// that was waiting when admission was opened, which open admission approves.
    /// </summary>
    private Task<AdmissionState> AdmitAsync(
        string provider, string subject, string display, CancellationToken ct)
        => db.InTransactionAsync<AdmissionState>(async (connection, transaction) =>
        {
            var now = clock.GetUtcNow();
            var opening = admission == AdmissionMode.Open ? AdmissionState.Approved : AdmissionState.Waiting;
            var shown = Truncate(display, IdentityDisplayWidth);

            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO admissions (provider, subject, state, display, requested_at, decided_at)
                VALUES (@provider, @subject, @state, @display, @now,
                        CASE WHEN @state = 'Approved' THEN @now END)
                ON DUPLICATE KEY UPDATE display = @display
                """,
                ("@provider", provider), ("@subject", subject), ("@state", opening),
                ("@display", shown), ("@now", now));

            // Locked, so an operator's decision made now is either seen here or waits for this to end.
            var state = await connection.ReadOneAsync(transaction,
                "SELECT state FROM admissions WHERE provider = @provider AND subject = @subject FOR UPDATE",
                reader => reader.Enum<AdmissionState>("state"),
                ("@provider", provider), ("@subject", subject));

            if (state == AdmissionState.Waiting && admission == AdmissionMode.Open)
            {
                await connection.ExecuteAsync(transaction,
                    """
                    UPDATE admissions SET state = 'Approved', decided_at = @now
                    WHERE provider = @provider AND subject = @subject
                    """,
                    ("@now", now), ("@provider", provider), ("@subject", subject));
                state = AdmissionState.Approved;
            }

            return state;
        }, ct);

    /// <summary>
    /// WARNING: creates the account WITHOUT asking the admission list - a sign-in must call
    /// <see cref="SignInAsync"/> instead, or anybody with a provider login gets an account.
    ///
    /// <para>The account of this identity, created on first sight. One transaction writes the user, the
    /// identity, the event line and the retention row, so no account exists half-made: a user
    /// without a stream row could never have an event allocated an ordinal.</para>
    ///
    /// <para>Two sign-ins of one identity at once (a double-clicked button, two tabs) must make one
    /// account. The identity's primary key is what decides, not a read made first: both callers
    /// would read "none yet" and both would create a user. The loser's insert waits on the winner's
    /// row, fails with a duplicate key once it commits, rolls back its own half-made user, and
    /// answers with the winner's id. The user row is inserted before the identity only because the
    /// identity's foreign key needs it to exist.</para>
    ///
    /// <para>Internal, and named for what it skips, so it is not the obvious call for the next sign-in
    /// somebody writes: the development sign-in and the tests use it, a provider's callback must not.</para>
    /// </summary>
    internal async Task<string> ProvisionWithoutAdmissionAsync(
        string provider, string subject, string display, CancellationToken ct)
    {
        var userId = Ids.New();
        var now = clock.GetUtcNow();

        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        try
        {
            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO users (id, display_name, status, created_at)
                VALUES (@id, @name, 'Active', @now)
                """,
                ("@id", userId), ("@name", Truncate(display, DisplayNameWidth)), ("@now", now));

            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO external_identities (provider, subject, user_id, display, created_at)
                VALUES (@provider, @subject, @user, @display, @now)
                """,
                ("@provider", provider), ("@subject", subject), ("@user", userId),
                ("@display", Truncate(display, IdentityDisplayWidth)), ("@now", now));

            await connection.ExecuteAsync(transaction,
                "INSERT INTO user_streams (owner_id) VALUES (@id)", ("@id", userId));
            await connection.ExecuteAsync(transaction,
                "INSERT INTO user_retention (owner_id) VALUES (@id)", ("@id", userId));

            await transaction.CommitAsync(ct);
            return userId;
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            // Another call provisioned this identity first. Only the identity's key can be what
            // collided: the ids are fresh and the stream and retention rows are keyed by them.
            await transaction.RollbackAsync(ct);
        }

        return await connection.ReadOneAsync(null,
            "SELECT user_id FROM external_identities WHERE provider = @provider AND subject = @subject",
            reader => reader.GetString("user_id"), ("@provider", provider), ("@subject", subject))
            ?? throw new InvalidOperationException(
                "The identity collided on insert and is gone on read: it was deleted between the two.");
    }

    /// <summary>
    /// The name the signed-in person is shown under, or null when their account is gone. Only their own:
    /// the account is the one their session names, never one the request asks about.
    /// </summary>
    public async Task<string?> DisplayNameAsync(UserAccess user, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        return await connection.ReadOneAsync(null,
            "SELECT display_name FROM users WHERE id = @id",
            reader => reader.GetString("display_name"), ("@id", user.UserId));
    }

    /// <summary>
    /// At most <paramref name="width"/> characters, never cutting a surrogate pair in half: a lone
    /// surrogate is not valid UTF-8, and the driver would send a replacement character in its place.
    /// </summary>
    private static string Truncate(string text, int width)
    {
        if (text.Length <= width)
        {
            return text;
        }

        var end = char.IsHighSurrogate(text[width - 1]) ? width - 1 : width;
        return text[..end];
    }
}
