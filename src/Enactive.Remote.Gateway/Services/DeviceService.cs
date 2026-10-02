namespace Enactive.Remote.Gateway.Services;

using System.Security.Cryptography;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>
/// A browser profile of one account and the P-256 key it will be sent grants for, and the grants
/// themselves.
///
/// <para>The gateway validates the key's SHAPE and never uses it: it cannot open a grant, only keep the
/// row that says which browser a grant is for. Every lookup names the owner, and another person's device
/// id is refused in exactly the words used for one that does not exist; a locking lookup goes through
/// <c>ux_devices_owner</c>, for the reason given on <see cref="UserService"/>.</para>
///
/// <para><b>Lock order for a grant.</b> The account (shared, a computer's call only), then each computer
/// named, for update, in id order; then each device named, shared, in id order; then the grant rows. No
/// other path locks a device and then a computer, and a device's revocation locks only the device before
/// deleting its grants, so the two wait for each other but never in a cycle.</para>
/// </summary>
public sealed class DeviceService(Database db, Limits limits, TimeProvider clock)
{
    // The width of devices.label. Longer is refused rather than cut: the label is what the person
    // recognises the browser by, and a silently shortened one may not be.
    private const int MaxLabel = 80;

    /// <summary>
    /// How often a visit is written down. The panel asks for its grants on every refresh, and each of
    /// those calls wrote the device's row; "last seen" is never shown finer than a minute.
    /// </summary>
    private static readonly TimeSpan VisitInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The most grants one call may carry. A rotation sends one per trusted device and an invitation one
    /// per epoch of each computer the browser holds keys of; fifty covers both with room. Without a bound
    /// one call held a computer's row locked for as many inserts as its body could hold.
    /// </summary>
    public const int MaxGrantsPerCall = 50;

    // What the cipher and the curve take. An uncompressed P-256 point; an AES-GCM nonce; a 32-byte key
    // wrapped with its 16-byte tag; an HMAC-SHA256; an ECDSA P-256 signature in IEEE P1363 form.
    private const int PointLength = 65;
    private const int NonceLength = 12;
    private const int WrappedKeyLength = 48;
    private const int HmacLength = 32;
    private const int SignatureLength = 64;

    // The pairing a computer's own connection code starts; every other pairing is an invitation's id.
    private static readonly string AuthByConnect = Grants.AuthByPairing("connect");

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
            // exclusive afterwards would deadlock instead of queueing. Locked first, they queue. The COUNT
            // below must stay AFTER this lock: the first consistent read of a REPEATABLE READ transaction
            // fixes its snapshot, so a count taken before the lock would not see a registration that
            // committed while this one waited.
            var accountExists = await connection.ExistsAsync(transaction,
                "SELECT 1 FROM users WHERE id = @owner FOR UPDATE", ("@owner", user.UserId));

            // The session check is made at the door, so the account can only be gone if it was deleted
            // since; a clean refusal, where the insert would have failed on its foreign key as a 500.
            if (!accountExists)
            {
                throw GatewayFault.Unauthenticated();
            }

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
    /// Removing one that is already removed succeeds, writes no second audit row, and sweeps any grant left
    /// behind, so a retried tap is harmless.
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

            var now = clock.GetUtcNow();

            if (!already.Value)
            {
                await connection.ExecuteAsync(transaction,
                    "UPDATE devices SET revoked_at = @now WHERE owner_id = @owner AND id = @device",
                    ("@now", now), ("@owner", user.UserId), ("@device", deviceId));

                await AuditAsync(connection, transaction, user, "device-revoked", deviceId, now);
            }

            // Deleted on a repeated revoke too. A grant written by a request that was already past its
            // own check when the device was revoked can land after the first revoke's delete; returning
            // early here would leave that grant for a removed device for ever. The delete is idempotent.
            await connection.ExecuteAsync(transaction,
                "DELETE FROM grants WHERE owner_id = @owner AND device_id = @device",
                ("@owner", user.UserId), ("@device", deviceId));
        }, ct);

    /// <summary>
    /// The device a call says it is made from, if it is this person's and still theirs. A removed device
    /// is refused with its own code so the panel can say so; anything else that is not this person's
    /// device is refused like a missing one. Records the visit, at most once in <see cref="VisitInterval"/>
    /// and not inside a lock: it is a hint for the person's device list, and losing a race with a
    /// revocation costs nothing.
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

        var now = clock.GetUtcNow();
        await connection.ExecuteAsync(null,
            """
            UPDATE devices SET last_seen_at = @now
            WHERE owner_id = @owner AND id = @device AND revoked_at IS NULL
              AND (last_seen_at IS NULL OR last_seen_at <= @stale)
            """,
            ("@now", now), ("@stale", now - VisitInterval), ("@owner", user.UserId), ("@device", deviceId));

        return new DeviceAccess(deviceId, user.UserId);
    }

    // ── grants ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A computer publishes grants of its own keys: the one answering its pairing, those answering an
    /// invitation it made, and the rotations it signed. Its newest epoch follows the newest grant, so the
    /// panel can tell a device that a newer key exists than the ones it was given.
    ///
    /// <para>The account and the computer are read again inside the transaction, as on every call of a
    /// computer's (see <see cref="HostService"/>): a credential revoked while the computer was connected
    /// stops it at its next call.</para>
    /// </summary>
    public Task PublishGrantsAsync(HostAccess host, IReadOnlyList<KeyGrant>? grants, CancellationToken ct)
    {
        var batch = Checked(grants, fromComputer: true);

        // Before the database is asked anything: a computer holds only its own keys, so a grant naming
        // another computer is its own mistake, whoever that other computer belongs to.
        for (var i = 0; i < batch.Count; i++)
        {
            if (!string.Equals(batch[i].Grant.HostId, host.HostId, StringComparison.Ordinal))
            {
                throw BadGrant(i, "hostId", "must be this computer's own id: a computer grants only its own keys.");
            }
        }

        return db.InTransactionAsync(async (connection, transaction) =>
        {
            var pins = new Dictionary<string, byte[]?>(StringComparer.Ordinal)
            {
                [host.HostId] = await LockComputerAsync(connection, transaction, host)
            };

            await StoreAsync(connection, transaction, host.OwnerId, batch, pins);

            if (batch.Count > 0)
            {
                // GREATEST, so an older grant sent late - a retried batch from before a rotation - does not
                // take the computer's newest epoch back.
                await connection.ExecuteAsync(transaction,
                    "UPDATE hosts SET key_epoch = GREATEST(key_epoch, @epoch) WHERE owner_id = @owner AND id = @host",
                    ("@epoch", batch.Max(item => item.Grant.Epoch)), ("@owner", host.OwnerId), ("@host", host.HostId));
            }
        }, ct);
    }

    /// <summary>
    /// A trusted browser passes keys it holds to another device of the same person's - or to itself -
    /// answering an invitation. Only under an invitation's pair key: the grant answering a computer's own
    /// pairing and the rotation grants are the computer's to make, and a browser sending one is mistaken or
    /// passing itself off as the computer. Every computer named must be the person's and not revoked.
    ///
    /// <para>The computer's newest epoch is not moved from here. A browser can only pass on keys the
    /// computer made, so an epoch above the computer's newest says nothing true about it, and the panel
    /// would show every device a key it can never be given.</para>
    /// </summary>
    public Task PublishGrantsAsync(DeviceAccess device, IReadOnlyList<KeyGrant>? grants, CancellationToken ct)
    {
        var batch = Checked(grants, fromComputer: false);

        return db.InTransactionAsync(async (connection, transaction) =>
        {
            var pins = new Dictionary<string, byte[]?>(StringComparer.Ordinal);

            // In id order, so two calls naming the same computers lock them in the same order.
            foreach (var hostId in batch.Select(item => item.Grant.HostId).Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                var computer = await LockHostRowAsync(connection, transaction, device.OwnerId, hostId);

                if (computer is null)
                {
                    throw GatewayFault.NotFound("That computer is not registered.");
                }

                if (computer.Value.Revoked)
                {
                    throw GatewayFault.NotFound("That computer has been revoked.");
                }

                pins[hostId] = computer.Value.SigningPublic;
            }

            await StoreAsync(connection, transaction, device.OwnerId, batch, pins);
        }, ct);
    }

    /// <summary>
    /// The grants made to this device, by computer and then by epoch, each computer with its newest epoch
    /// beside them so the panel can see a key it was not given without asking again. A revoked computer's
    /// are left out: the panel no longer offers that computer, and its keys are not handed out after it
    /// was cut off.
    /// </summary>
    public async Task<IReadOnlyList<HostGrants>> ReadGrantsAsync(DeviceAccess device, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        var rows = await connection.ReadAllAsync(null,
            """
            SELECT g.host_id, h.key_epoch, g.grant_json
            FROM grants g
            JOIN hosts h ON h.owner_id = g.owner_id AND h.id = g.host_id
            WHERE g.owner_id = @owner AND g.device_id = @device AND h.revoked = 0
            ORDER BY g.host_id, g.epoch
            """,
            reader => (
                HostId: reader.GetString("host_id"),
                KeyEpoch: reader.GetUInt32("key_epoch"),
                Grant: RemoteJson.Deserialize<KeyGrant>(reader.GetString("grant_json"))),
            ("@owner", device.OwnerId), ("@device", device.DeviceId));

        return rows
            .GroupBy(row => row.HostId, StringComparer.Ordinal)
            .Select(group => new HostGrants(group.Key, group.First().KeyEpoch, group.Select(row => row.Grant).ToList()))
            .ToList();
    }

    /// <summary>
    /// The account is active and the computer live, as <see cref="HostService"/> checks on every call of a
    /// computer's - but the computer's row is locked for update, not shared. A publish writes that row (its
    /// newest epoch, and its signing key at the first grant), and a shared lock would have to become an
    /// exclusive one: two publishes of one computer, each holding the row shared and each waiting for the
    /// other to let go of it, deadlocked. Exclusive from the start they queue, and a browser's grant for the
    /// same computer queues with them, so two first grants cannot both pin a key.
    /// </summary>
    private static async Task<byte[]?> LockComputerAsync(
        MySqlConnection connection, MySqlTransaction transaction, HostAccess host)
    {
        var status = await connection.ReadOneAsync(transaction,
            "SELECT status FROM users WHERE id = @owner FOR SHARE",
            reader => reader.GetString("status"), ("@owner", host.OwnerId));

        // Not there at all is a computer whose account is gone, and its row with it: the unknown-computer
        // refusal below.
        if (status is not null && status != "Active")
        {
            throw GatewayFault.AccountDisabled();
        }

        var computer = await LockHostRowAsync(connection, transaction, host.OwnerId, host.HostId);

        if (computer is null)
        {
            throw GatewayFault.UnknownHost();
        }

        if (computer.Value.Revoked)
        {
            throw GatewayFault.HostRevoked();
        }

        return computer.Value.SigningPublic;
    }

    /// <summary>
    /// A computer's row, locked for update, through the key that starts with the owner: through the primary
    /// key Bob naming Alice's computer locked her row before the owner filter refused it (see
    /// <see cref="UserService"/>).
    /// </summary>
    private static Task<(bool Revoked, byte[]? SigningPublic)?> LockHostRowAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId, string hostId)
        => connection.ReadOneAsync(transaction,
            """
            SELECT revoked, signing_public FROM hosts FORCE INDEX (ux_hosts_owner)
            WHERE owner_id = @owner AND id = @host
            FOR UPDATE
            """,
            reader => ((bool Revoked, byte[]? SigningPublic)?)(
                reader.GetBoolean("revoked"),
                reader.IsDBNull(reader.GetOrdinal("signing_public")) ? null : (byte[])reader["signing_public"]),
            ("@owner", ownerId), ("@host", hostId));

    /// <summary>
    /// Every device named is the owner's and not removed, the computer's signing key is pinned or matched,
    /// and the grants are written - a grant sent again for the same computer, device and epoch replacing the
    /// earlier one, because a rotation can be re-sent and the last one sent is what the device needs.
    /// </summary>
    private async Task StoreAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId, List<CheckedGrant> batch,
        Dictionary<string, byte[]?> pins)
    {
        // Locked for share, in this transaction: a revocation holds the device's row until it commits, so a
        // grant to that device waits for it and then finds it removed. Read without a lock, a grant arriving
        // while the revocation was under way saw the device still live and was stored - the foreign key does
        // not wait for the revocation either, since it changes no column the key reads - and it outlived the
        // revocation's sweep of the device's grants.
        // In id order, so two calls naming the same devices lock them in the same order.
        foreach (var deviceId in batch.Select(item => item.Grant.DeviceId).Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var revoked = await connection.ReadOneAsync(transaction,
                """
                SELECT revoked_at IS NOT NULL FROM devices FORCE INDEX (ux_devices_owner)
                WHERE owner_id = @owner AND id = @device
                FOR SHARE
                """,
                reader => (bool?)reader.GetBoolean(0), ("@owner", ownerId), ("@device", deviceId));

            if (revoked is null)
            {
                throw NoSuchDevice();
            }

            if (revoked.Value)
            {
                throw GatewayFault.NotFound("That device was removed from the account.");
            }
        }

        var now = clock.GetUtcNow();

        for (var i = 0; i < batch.Count; i++)
        {
            var (grant, signingPublic) = batch[i];
            var pinned = pins[grant.HostId];

            // The first grant stored for a computer fixes its signing key, and every later one must carry the
            // same. Devices pin the key at their own first grant and refuse any other, so this changes nothing
            // they would accept; it only makes a computer or a panel that sends the wrong key find out now,
            // rather than through a device that silently refuses the grant later.
            if (pinned is null)
            {
                await connection.ExecuteAsync(transaction,
                    "UPDATE hosts SET signing_public = @key WHERE owner_id = @owner AND id = @host",
                    ("@key", signingPublic), ("@owner", ownerId), ("@host", grant.HostId));
                pins[grant.HostId] = signingPublic;
            }
            else if (!pinned.AsSpan().SequenceEqual(signingPublic))
            {
                throw BadGrant(i, "hostSigningPublic",
                    "is not the signing key this computer's earlier grants carry, and devices refuse any other.");
            }

            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO grants (owner_id, host_id, device_id, epoch, grant_json, created_at)
                VALUES (@owner, @host, @device, @epoch, @json, @now)
                ON DUPLICATE KEY UPDATE grant_json = @json, created_at = @now
                """,
                ("@owner", ownerId), ("@host", grant.HostId), ("@device", grant.DeviceId), ("@epoch", grant.Epoch),
                ("@json", RemoteJson.Serialize(grant)), ("@now", now));
        }
    }

    /// <summary>
    /// The shape of every grant in a call, checked before anything is read or written; one that fails
    /// refuses the whole call, so a batch is never half stored. The gateway cannot open a grant and does not
    /// try - each device verifies its own - so this is no defence against a forged one. It turns a broken
    /// grant into a refusal its sender sees, instead of a row a device fails to open later with nobody
    /// watching.
    /// </summary>
    private static List<CheckedGrant> Checked(IReadOnlyList<KeyGrant>? grants, bool fromComputer)
    {
        if (grants is null)
        {
            throw GatewayFault.BadGrant("The body must be a list of grants.");
        }

        if (grants.Count > MaxGrantsPerCall)
        {
            throw GatewayFault.BadGrant($"A call carries at most {MaxGrantsPerCall} grants; send the rest in another.");
        }

        var batch = new List<CheckedGrant>(grants.Count);

        for (var i = 0; i < grants.Count; i++)
        {
            var grant = grants[i] ?? throw GatewayFault.BadGrant($"Grant {i + 1} is empty.");

            RequireId(i, grant.HostId, "hostId");
            RequireId(i, grant.DeviceId, "deviceId");

            if (grant.Epoch < 1)
            {
                throw BadGrant(i, "epoch", "must be 1 or more: a computer's first key is epoch 1.");
            }

            RequireBytes(i, grant.EphemeralPublic, PointLength, "ephemeralPublic");
            RequireBytes(i, grant.Nonce, NonceLength, "nonce");
            RequireBytes(i, grant.Ciphertext, WrappedKeyLength, "ciphertext");

            var authBy = grant.AuthBy ?? "";
            var invitation = IsInvitation(authBy);

            if (!fromComputer && !invitation)
            {
                throw BadGrant(i, "authBy",
                    "must be 'pair:' and an invitation's 32 lowercase hex characters: the grant answering a "
                    + "computer's own pairing, and a rotation, are the computer's to make.");
            }

            if (!invitation && authBy != AuthByConnect && authBy != Grants.AuthByHost)
            {
                throw BadGrant(i, "authBy",
                    $"must be '{AuthByConnect}', '{Grants.AuthByHost}', or 'pair:' and 32 lowercase hex characters.");
            }

            RequireBytes(i, grant.Mac, authBy == Grants.AuthByHost ? SignatureLength : HmacLength, "mac");

            var signingPublic = RequireBytes(i, grant.HostSigningPublic, PointLength, "hostSigningPublic");

            // Only a point on the curve: it is pinned for the computer, and a pinned key that is not one
            // would have every later grant of that computer refused here.
            try
            {
                using var imported = P256.ImportSigningPublic(signingPublic);
            }
            catch (CryptographicException)
            {
                throw BadGrant(i, "hostSigningPublic", "must be an uncompressed P-256 public key.");
            }

            batch.Add(new CheckedGrant(grant, signingPublic));
        }

        return batch;
    }

    /// <summary>
    /// An invitation's pairing: <c>pair:</c> and 32 lowercase hex characters, compared character by
    /// character. A pattern ending in <c>$</c> also matches before a final newline, and "pair:connect\n" is
    /// not a pairing any device made.
    /// </summary>
    private static bool IsInvitation(string authBy)
    {
        const string prefix = "pair:";
        return authBy.Length == prefix.Length + 32
            && authBy.StartsWith(prefix, StringComparison.Ordinal)
            && !authBy.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef");
    }

    /// <summary>
    /// An id as the gateway makes them, 32 lowercase hex characters. Checked here because the id columns
    /// compare ignoring trailing spaces: "id " found the computer, and the grant was stored naming an id no
    /// device would ask for.
    /// </summary>
    private static void RequireId(int index, string? id, string field)
    {
        if (id is not { Length: 32 } || id.AsSpan().ContainsAnyExcept("0123456789abcdef"))
        {
            throw BadGrant(index, field, "must be an id as this service issues them: 32 lowercase hex characters.");
        }
    }

    private static byte[] RequireBytes(int index, string? text, int length, string field)
    {
        byte[] bytes;

        try
        {
            bytes = B64.FromUrl(text ?? "");
        }
        catch (CryptographicException)
        {
            bytes = [];
        }

        return bytes.Length == length
            ? bytes
            : throw BadGrant(index, field, $"must be {length} bytes, as base64url text.");
    }

    // Numbered from 1 and naming the field, so the sender can tell which grant of a batch was refused, and why.
    private static GatewayFault BadGrant(int index, string field, string why)
        => GatewayFault.BadGrant($"Grant {index + 1}: '{field}' {why}");

    private sealed record CheckedGrant(KeyGrant Grant, byte[] SigningPublic);

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

/// <summary>
/// The grants made to one device for one computer, oldest epoch first, with that computer's newest epoch: a
/// device holding no grant of that epoch has not been given the newest key yet.
/// </summary>
public sealed record HostGrants(string HostId, uint KeyEpoch, IReadOnlyList<KeyGrant> Grants);

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

    /// <summary>
    /// The device id the request names, or the one refusal every device-bound endpoint gives when it
    /// names none.
    /// </summary>
    public static string RequireDeviceId(this HttpContext context)
        => context.DeviceIdOrNull() ?? throw GatewayFault.DeviceHeaderMissing();
}
