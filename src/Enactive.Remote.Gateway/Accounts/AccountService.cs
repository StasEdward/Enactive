namespace Enactive.Remote.Gateway.Accounts;

using System.Globalization;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>People of the gateway: how an account comes to exist.</summary>
public sealed class AccountService(Database db, TimeProvider clock)
{
    // The widths of users.display_name and external_identities.display. A longer name is cut here,
    // because the database would otherwise refuse the sign-in of anyone whose provider hands back a
    // long display name - the person could never get an account at all.
    private const int DisplayNameWidth = 100;
    private const int IdentityDisplayWidth = 200;

    /// <summary>
    /// The account of this identity, created on first sight. One transaction writes the user, the
    /// identity, the event line and the retention row, so no account exists half-made: a user
    /// without a stream row could never have an event allocated an ordinal.
    ///
    /// <para>Two sign-ins of one identity at once (a double-clicked button, two tabs) must make one
    /// account. The identity's primary key is what decides, not a read made first: both callers
    /// would read "none yet" and both would create a user. The loser's insert waits on the winner's
    /// row, fails with a duplicate key once it commits, rolls back its own half-made user, and
    /// answers with the winner's id. The user row is inserted before the identity only because the
    /// identity's foreign key needs it to exist.</para>
    /// </summary>
    public async Task<string> ProvisionAsync(
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
