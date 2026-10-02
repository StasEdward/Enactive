namespace Enactive.Remote.Gateway.Services;

using System.Security.Cryptography;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>
/// A browser profile of one account and the P-256 key it will be sent grants for.
///
/// <para>The gateway validates the key's SHAPE and never uses it: it cannot open a grant, only keep the
/// row that says which browser a grant is for. Every lookup names the owner, and another person's device
/// id is refused in exactly the words used for one that does not exist; a locking lookup goes through
/// <c>ux_devices_owner</c>, for the reason given on <see cref="UserService"/>.</para>
/// </summary>
public sealed class DeviceService(Database db, Limits limits, TimeProvider clock)
{
    // The width of devices.label. Longer is refused rather than cut: the label is what the person
    // recognises the browser by, and a silently shortened one may not be.
    private const int MaxLabel = 80;

    /// <summary>
    /// Adds a browser. The key is checked for shape and curve here, so a caller that did not decode it
    /// the way the endpoint does still cannot store a key no grant could be sealed to.
    /// </summary>
    public async Task<string> RegisterAsync(UserAccess user, byte[] publicKey, string? label, CancellationToken ct)
    {
        var name = Label(label);

        try
        {
            using var imported = P256.ImportPublic(publicKey);
        }
        catch (CryptographicException)
        {
            throw GatewayFault.BadKey();
        }

        var id = Ids.New();

        await db.InTransactionAsync(async (connection, transaction) =>
        {
            // The account's row is the lock the count is taken under. Counting alone does not stop two
            // registrations that both read "one place left" and both insert; and the insert itself takes a
            // shared lock on this row for its foreign key, so two of them taking that first and wanting it
            // exclusive afterwards would deadlock instead of queueing. Locked first, they queue.
            await connection.ExistsAsync(transaction,
                "SELECT 1 FROM users WHERE id = @owner FOR UPDATE", ("@owner", user.UserId));

            // Removed devices do not count: removing one is how a person makes room for another.
            var held = await connection.ReadOneAsync(transaction,
                "SELECT COUNT(*) FROM devices WHERE owner_id = @owner AND revoked_at IS NULL",
                reader => reader.GetInt64(0), ("@owner", user.UserId));

            if (held >= limits.DevicesPerUser)
            {
                throw GatewayFault.DeviceLimit(limits.DevicesPerUser);
            }

            var now = clock.GetUtcNow();
            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO devices (id, owner_id, public_key, label, created_at)
                VALUES (@id, @owner, @key, @label, @now)
                """,
                ("@id", id), ("@owner", user.UserId), ("@key", publicKey), ("@label", name), ("@now", now));

            await AuditAsync(connection, transaction, user, "device-registered", id, now);
        }, ct);

        return id;
    }

    /// <summary>The person's own devices, oldest first, removed ones included so the panel can say so.</summary>
    public async Task<IReadOnlyList<DeviceInfo>> ListAsync(UserAccess user, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        return await connection.ReadAllAsync(null,
            """
            SELECT id, label, public_key, created_at, last_seen_at, revoked_at
            FROM devices WHERE owner_id = @owner ORDER BY created_at, id
            """,
            reader => new DeviceInfo(
                reader.GetString("id"),
                reader.GetString("label"),
                B64.Url((byte[])reader["public_key"]),
                reader.Utc("created_at"),
                reader.UtcOrNull("last_seen_at"),
                !reader.IsDBNull(reader.GetOrdinal("revoked_at"))),
            ("@owner", user.UserId));
    }

    /// <summary>
    /// Removes a browser from the account and deletes the grants made to it, in one transaction. The
    /// schema deletes grants when a device row is deleted, but a revoked device keeps its row (so the
    /// panel can still say it was removed), and without this its keys would outlive its removal.
    /// Removing one that is already removed succeeds and changes nothing, so a retried tap is harmless.
    /// </summary>
    public Task RevokeAsync(UserAccess user, string deviceId, CancellationToken ct)
        => db.InTransactionAsync(async (connection, transaction) =>
        {
            // FORCE INDEX: through the primary key on id this would lock another person's row first and
            // apply the owner filter afterwards, so Bob asking for Alice's id would wait on her
            // transactions - and learn the id exists.
            var already = await connection.ReadOneAsync(transaction,
                """
                SELECT revoked_at IS NOT NULL FROM devices FORCE INDEX (ux_devices_owner)
                WHERE owner_id = @owner AND id = @device
                FOR UPDATE
                """,
                reader => (bool?)reader.GetBoolean(0), ("@owner", user.UserId), ("@device", deviceId));

            if (already is null)
            {
                throw NoSuchDevice();
            }

            if (already.Value)
            {
                return;
            }

            var now = clock.GetUtcNow();
            await connection.ExecuteAsync(transaction,
                "UPDATE devices SET revoked_at = @now WHERE owner_id = @owner AND id = @device",
                ("@now", now), ("@owner", user.UserId), ("@device", deviceId));

            await connection.ExecuteAsync(transaction,
                "DELETE FROM grants WHERE owner_id = @owner AND device_id = @device",
                ("@owner", user.UserId), ("@device", deviceId));

            await AuditAsync(connection, transaction, user, "device-revoked", deviceId, now);
        }, ct);

    /// <summary>
    /// The device a call says it is made from, if it is this person's and still theirs. A removed device
    /// is refused with its own code so the panel can say so; anything else that is not this person's
    /// device is refused like a missing one. Records the visit, but not inside a lock: it is a hint for
    /// the person's device list, and losing a race with a revocation costs nothing.
    /// </summary>
    public async Task<DeviceAccess> RequireAsync(UserAccess user, string deviceId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        var revoked = await connection.ReadOneAsync(null,
            "SELECT revoked_at IS NOT NULL FROM devices WHERE owner_id = @owner AND id = @device",
            reader => (bool?)reader.GetBoolean(0), ("@owner", user.UserId), ("@device", deviceId));

        if (revoked is null)
        {
            throw NoSuchDevice();
        }

        if (revoked.Value)
        {
            throw GatewayFault.DeviceRevoked();
        }

        await connection.ExecuteAsync(null,
            "UPDATE devices SET last_seen_at = @now WHERE owner_id = @owner AND id = @device AND revoked_at IS NULL",
            ("@now", clock.GetUtcNow()), ("@owner", user.UserId), ("@device", deviceId));

        return new DeviceAccess(deviceId, user.UserId);
    }

    /// <summary>
    /// A key as the panel sends it, decoded. Text that is not base64url is a bad key, like one of the
    /// wrong length: the browser sent something that is not a key, and which way it failed does not matter.
    /// </summary>
    public static byte[] DecodeKey(string? text)
    {
        try
        {
            return B64.FromUrl(text ?? "");
        }
        catch (CryptographicException)
        {
            throw GatewayFault.BadKey();
        }
    }

    // The same sentence for a foreign id as for a missing one: a refusal that differed would answer
    // "does somebody else have a device by that id?".
    private static GatewayFault NoSuchDevice() => GatewayFault.NotFound("That device is not registered.");

    /// <summary>
    /// Trimmed, 1 to 80 characters, no control characters. A label is shown back to the person on every
    /// screen that lists devices, and one holding a newline or an escape character could break that
    /// line or draw another one.
    /// </summary>
    private static string Label(string? label)
    {
        var trimmed = label?.Trim() ?? "";

        if (trimmed.Length is 0 or > MaxLabel || trimmed.Any(char.IsControl))
        {
            throw GatewayFault.BadRequest(
                $"'label' must be 1 to {MaxLabel:N0} characters, without control characters.");
        }

        return trimmed;
    }

    private static Task AuditAsync(
        MySqlConnection connection, MySqlTransaction transaction, UserAccess user, string action,
        string target, DateTimeOffset now)
        => connection.ExecuteAsync(transaction,
            "INSERT INTO audit (owner_id, at, actor, action, target) VALUES (@owner, @at, @actor, @action, @target)",
            ("@owner", user.UserId), ("@at", now), ("@actor", "user:" + user.UserId), ("@action", action),
            ("@target", target));
}

/// <summary>A device as the panel lists it. The key is base64url text, as it was registered.</summary>
public sealed record DeviceInfo(
    string Id, string Label, string PublicKey, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool Revoked);

/// <summary>Reads which device a call says it is made from.</summary>
public static class DeviceHeader
{
    public const string Name = "X-Enactive-Device";

    /// <summary>
    /// The device id the request names, or null when it names none. Whether it is a device of the caller's
    /// is <see cref="DeviceService.RequireAsync"/>'s to decide; this only reads the header.
    /// </summary>
    public static string? DeviceIdOrNull(this HttpContext context)
    {
        var value = context.Request.Headers[Name].ToString().Trim();
        return value.Length == 0 ? null : value;
    }
}
