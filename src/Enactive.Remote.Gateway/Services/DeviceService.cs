namespace Enactive.Remote.Gateway.Services;

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Accounts;
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
/// deleting its grants - a computer's revocation of one takes the account and the computer shared first,
/// in the same order - so the two wait for each other but never in a cycle.</para>
///
/// <para><b>Lock order for an invitation.</b> The account (for update when an invitation is made, shared on a
/// computer's other calls), then the computer or the browser making the call, shared; then the invitation;
/// then the device answering it, shared; then its enrollment. Answering an invitation locks no account and no
/// computer, and the device locks it takes are shared ones, which only a revocation - locking nothing but the
/// device - waits on.</para>
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

    /// <summary>
    /// How many device rows an account keeps, as a multiple of its device limit. A removed device keeps its
    /// row so the panel can say it was removed, so a script that adds and removes devices grew the table, and
    /// the device list, without bound. Five times the limit keeps every live device and the recently removed
    /// ones; the longest-removed go first.
    /// </summary>
    internal const int RowsPerAllowedDevice = 5;

    // The pairing a computer's own connection code starts; every other pairing is an invitation's id.
    private static readonly string AuthByConnect = Grants.AuthByPairing("connect");

    /// <summary>
    /// Adds a browser. The key is checked for shape and curve here, so a caller that did not decode it
    /// the way the endpoint does still cannot store a key no grant could be sealed to.
    ///
    /// <para>A key this account already holds on a live device answers with that device, and nothing is
    /// written. A browser whose registration was answered but never heard - the tab closed, the network
    /// dropped - registers its key again, and a second row took a second place of the allowance for one
    /// browser, or refused the retry outright on a full account. A removed device's key is not looked for:
    /// removing a device must not be undone by registering it again.</para>
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

        return await db.InTransactionAsync(async (connection, transaction) =>
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

            // Under the account's lock, like the count: two tabs of one browser registering at once find
            // each other's row instead of both inserting. Before the limit, so a full account still answers
            // a browser that is only asking again.
            var existing = await connection.ReadOneAsync(transaction,
                """
                SELECT id FROM devices
                WHERE owner_id = @owner AND public_key = @key AND revoked_at IS NULL
                ORDER BY created_at, id LIMIT 1
                """,
                reader => reader.GetString(0), ("@owner", user.UserId), ("@key", publicKey));

            if (existing is not null)
            {
                return existing;
            }

            // Removed devices do not count: removing one is how a person makes room for another.
            var held = await connection.ReadOneAsync(transaction,
                "SELECT COUNT(*) FROM devices WHERE owner_id = @owner AND revoked_at IS NULL",
                reader => reader.GetInt64(0), ("@owner", user.UserId));

            if (held >= limits.DevicesPerUser)
            {
                throw GatewayFault.DeviceLimit(limits.DevicesPerUser);
            }

            await PruneRemovedAsync(connection, transaction, user.UserId);

            var id = Ids.New();
            var now = clock.GetUtcNow();
            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO devices (id, owner_id, public_key, label, created_at)
                VALUES (@id, @owner, @key, @label, @now)
                """,
                ("@id", id), ("@owner", user.UserId), ("@key", publicKey), ("@label", name), ("@now", now));

            await Audit.WriteAsync(
                connection, transaction, now, user.UserId, Audit.User(user.UserId), Audit.DeviceRegistered, id);
            return id;
        }, ct);
    }

    /// <summary>
    /// Makes room for one more row within <see cref="RowsPerAllowedDevice"/> times the limit, by deleting the
    /// longest-removed devices; their grants and enrollments go with them by the schema's cascade. Called
    /// under the account's lock, when a device is added, which is the only thing that adds a row.
    ///
    /// <para>The rows are chosen by a plain read and deleted by their keys. A deleting scan over the account's
    /// devices would lock every row it read, the live ones too, and wait behind a removal in progress - which
    /// itself waits for the account's row, held here, to write its audit line: a deadlock.</para>
    /// </summary>
    private async Task PruneRemovedAsync(MySqlConnection connection, MySqlTransaction transaction, string ownerId)
    {
        var rows = await connection.ReadOneAsync(transaction,
            "SELECT COUNT(*) FROM devices WHERE owner_id = @owner",
            reader => reader.GetInt64(0), ("@owner", ownerId));

        var surplus = rows + 1 - KeptRows;

        if (surplus <= 0)
        {
            return;
        }

        var oldest = await connection.ReadAllAsync(transaction,
            $"""
            SELECT id FROM devices
            WHERE owner_id = @owner AND revoked_at IS NOT NULL
            ORDER BY revoked_at, created_at, id
            LIMIT {surplus}
            """,
            reader => reader.GetString(0), ("@owner", ownerId));

        foreach (var id in oldest)
        {
            await connection.ExecuteAsync(transaction,
                "DELETE FROM devices WHERE owner_id = @owner AND id = @id AND revoked_at IS NOT NULL",
                ("@owner", ownerId), ("@id", id));
        }
    }

    /// <summary>The rows an account keeps; in 64 bits, because the limit may be as large as an int.</summary>
    private long KeptRows => (long)limits.DevicesPerUser * RowsPerAllowedDevice;

    /// <summary>
    /// The person's own devices, oldest first, removed ones included so the panel can say so.
    ///
    /// <para>At most <see cref="RowsPerAllowedDevice"/> times the limit, the live devices and then the newest
    /// first: pruning keeps the table to that, but rows left from before a lower limit are pruned only as
    /// devices are added, and one answer must not be as long as the table meanwhile.</para>
    /// </summary>
    public async Task<IReadOnlyList<DeviceInfo>> ListAsync(UserAccess user, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        return await connection.ReadAllAsync(null,
            $"""
            SELECT id, label, public_key, created_at, last_seen_at, revoked_at
            FROM (
              SELECT id, label, public_key, created_at, last_seen_at, revoked_at
              FROM devices WHERE owner_id = @owner
              ORDER BY revoked_at IS NULL DESC, created_at DESC, id DESC
              LIMIT {KeptRows}
            ) AS kept
            ORDER BY created_at, id
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
        => db.InTransactionAsync(
            (connection, transaction) => RevokeAsync(
                connection, transaction, user.UserId, Audit.User(user.UserId), deviceId),
            ct);

    /// <summary>
    /// A computer removes a device of its owner's ("Remove" in the desktop's trusted list), as the person would
    /// in a browser: the device is refused from then on and its grants are deleted. The computer has already
    /// stopped granting it keys; without this the gateway went on serving it the old ones, and its calls.
    /// Another person's device is refused like a missing one; the account and the computer are read again
    /// first, as on every call of a computer's, and the device is locked after them.
    /// </summary>
    public Task RevokeByComputerAsync(HostAccess host, string? deviceId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            throw NoSuchDevice();
        }

        return db.InTransactionAsync(async (connection, transaction) =>
        {
            await AuthorizeComputerAsync(connection, transaction, host, lockAccount: false);
            await RevokeAsync(connection, transaction, host.OwnerId, Audit.Host(host.HostId), deviceId);
        }, ct);
    }

    private async Task RevokeAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId, string actor, string deviceId)
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
            reader => (bool?)reader.GetBoolean(0), ("@owner", ownerId), ("@device", deviceId));

        if (already is null)
        {
            throw NoSuchDevice();
        }

        var now = clock.GetUtcNow();

        if (!already.Value)
        {
            await connection.ExecuteAsync(transaction,
                "UPDATE devices SET revoked_at = @now WHERE owner_id = @owner AND id = @device",
                ("@now", now), ("@owner", ownerId), ("@device", deviceId));

            await Audit.WriteAsync(connection, transaction, now, ownerId, actor, Audit.DeviceRevoked, deviceId);
        }

        // Deleted on a repeated revoke too. A grant written by a request that was already past its
        // own check when the device was revoked can land after the first revoke's delete; returning
        // early here would leave that grant for a removed device for ever. The delete is idempotent.
        await connection.ExecuteAsync(transaction,
            "DELETE FROM grants WHERE owner_id = @owner AND device_id = @device",
            ("@owner", ownerId), ("@device", deviceId));
    }

    /// <summary>
    /// The device a call says it is made from, if it is this person's and still theirs. A removed device
    /// is refused with its own code so the panel can say so; anything else that is not this person's
    /// device is refused like a missing one. Records the visit, at most once in <see cref="VisitInterval"/>
    /// and not inside a lock: it is a hint for the person's device list, and losing a race with a
    /// revocation costs nothing.
    /// </summary>
    public Task<DeviceAccess> RequireAsync(UserAccess user, string deviceId, CancellationToken ct)
        => RequireAsync(user, deviceId, NoSuchDevice, ct);

    /// <summary>
    /// The browser a call of the person's API is made from, from its <see cref="DeviceHeader"/>: one of theirs and
    /// not removed. Looked up once per request and kept on it, so an endpoint that needs the device asks again
    /// for nothing.
    ///
    /// <para>A device that is not the caller's is refused as removed, not as missing: to the browser that
    /// names it, it is gone either way, and the panel shows the same screen for both. Answered "not found", a
    /// browser whose device had been deleted with its rows was shown a failing panel with no word of why.</para>
    /// </summary>
    public async Task<DeviceAccess> CallerAsync(HttpContext context, CancellationToken ct)
    {
        if (context.Items[typeof(DeviceAccess)] is DeviceAccess known)
        {
            return known;
        }

        var device = await RequireAsync(context.UserAccess(), context.RequireDeviceId(), GatewayFault.DeviceRevoked, ct);
        context.Items[typeof(DeviceAccess)] = device;
        return device;
    }

    private async Task<DeviceAccess> RequireAsync(
        UserAccess user, string deviceId, Func<GatewayFault> unknown, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        var revoked = await connection.ReadOneAsync(null,
            "SELECT revoked_at IS NOT NULL FROM devices WHERE owner_id = @owner AND id = @device",
            reader => (bool?)reader.GetBoolean(0), ("@owner", user.UserId), ("@device", deviceId));

        if (revoked is null)
        {
            throw unknown();
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

            await StoreAsync(connection, transaction, host.OwnerId, batch, pins, fromComputer: true);

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
    /// <para>A browser passes on keys the computer made, and nothing more: the computer must have granted
    /// a key already, the grant must carry the signing key the computer's own grants pinned, its epoch may
    /// not be newer than the computer's newest, and it never replaces a grant already stored. The computer's
    /// newest epoch is not moved from here.</para>
    /// </summary>
    public Task PublishGrantsAsync(DeviceAccess device, IReadOnlyList<KeyGrant>? grants, CancellationToken ct)
    {
        var batch = Checked(grants, fromComputer: false);

        return db.InTransactionAsync(async (connection, transaction) =>
        {
            var pins = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            var newest = new Dictionary<string, uint>(StringComparer.Ordinal);

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

                // Only the computer pins its signing key. When a browser's grant could be the first stored, it
                // pinned whatever key it carried, and any signed-in session - one with no trusted device at
                // all - could register a computer's key before the computer did; the computer's own pairing
                // grant was then refused as carrying another key, for good.
                if (computer.Value.SigningPublic is null)
                {
                    throw BadGrant(batch.FindIndex(item => item.Grant.HostId == hostId), "hostId",
                        "names a computer that has not granted any key yet: its first grant is its own to make.");
                }

                pins[hostId] = computer.Value.SigningPublic;
                newest[hostId] = computer.Value.KeyEpoch;
            }

            // A key newer than the computer's newest is not one the computer made, and a device given it would
            // hold a key nothing is sealed with.
            for (var i = 0; i < batch.Count; i++)
            {
                if (batch[i].Grant.Epoch > newest[batch[i].Grant.HostId])
                {
                    throw BadGrant(i, "epoch",
                        $"is newer than the newest key this computer has granted ({newest[batch[i].Grant.HostId]}).");
                }
            }

            await StoreAsync(connection, transaction, device.OwnerId, batch, pins, fromComputer: false);
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
    /// other to let go of it, deadlocked. Exclusive from the start they queue, so two first grants cannot
    /// both pin a key, and a browser's grant for the same computer queues with them and reads the pin and
    /// the newest epoch they left.
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
    private static Task<(bool Revoked, byte[]? SigningPublic, uint KeyEpoch)?> LockHostRowAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId, string hostId)
        => connection.ReadOneAsync(transaction,
            """
            SELECT revoked, signing_public, key_epoch FROM hosts FORCE INDEX (ux_hosts_owner)
            WHERE owner_id = @owner AND id = @host
            FOR UPDATE
            """,
            reader => ((bool Revoked, byte[]? SigningPublic, uint KeyEpoch)?)(
                reader.GetBoolean("revoked"),
                reader.IsDBNull(reader.GetOrdinal("signing_public")) ? null : (byte[])reader["signing_public"],
                reader.GetUInt32("key_epoch")),
            ("@owner", ownerId), ("@host", hostId));

    /// <summary>
    /// Every device named is the owner's and not removed, the computer's signing key is pinned or matched,
    /// and the grants are written. The computer's grant sent again for the same computer, device and epoch
    /// replaces the earlier one, because a rotation can be re-sent and the last one sent is what the device
    /// needs. A browser's never replaces one: it would put a grant of the browser's making where the
    /// computer's was, and a device that cannot open it loses the key it had been given.
    /// </summary>
    private async Task StoreAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId, List<CheckedGrant> batch,
        Dictionary<string, byte[]?> pins, bool fromComputer)
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

            // The computer's first grant fixes its signing key, and every later one must carry the same. Devices
            // pin the key at their own first grant and refuse any other, so this changes nothing they would
            // accept; it only makes a computer or a panel that sends the wrong key find out now, rather than
            // through a device that silently refuses the grant later. Only the computer's own path can find
            // nothing pinned: the browser's refuses a computer that has granted no key before it gets here.
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

            // The computer's row is locked for update, and every grant of that computer is written under that
            // lock, so nothing can insert this grant between the check and the insert.
            if (!fromComputer && await connection.ExistsAsync(transaction,
                    """
                    SELECT 1 FROM grants
                    WHERE owner_id = @owner AND host_id = @host AND device_id = @device AND epoch = @epoch
                    """,
                    ("@owner", ownerId), ("@host", grant.HostId), ("@device", grant.DeviceId), ("@epoch", grant.Epoch)))
            {
                throw GatewayFault.Conflict(
                    $"Grant {i + 1}: that device already holds a grant of this computer's key for epoch {grant.Epoch}, "
                    + "and a browser does not replace one.");
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

            var ephemeral = RequireBytes(i, grant.EphemeralPublic, PointLength, "ephemeralPublic");

            // A point on the curve, as the signing key below and every device key are: no key can be agreed
            // with one that is not, and the device the grant was meant for would never open it.
            try
            {
                using var imported = P256.ImportPublic(ephemeral);
            }
            catch (CryptographicException)
            {
                throw BadGrant(i, "ephemeralPublic", "must be an uncompressed P-256 public key.");
            }

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
        if (!IsId(id))
        {
            throw BadGrant(index, field, "must be an id as this service issues them: 32 lowercase hex characters.");
        }
    }

    private static bool IsId([NotNullWhen(true)] string? id)
        => id is { Length: 32 } && !id.AsSpan().ContainsAnyExcept("0123456789abcdef");

    /// <summary>
    /// The bytes of a field, of the given length, in the one spelling <see cref="B64.Url"/> writes. The
    /// decoder also takes other spellings of the same bytes - with padding, for one - and the gateway compares
    /// the signing key as bytes where a device compares it as text: a second spelling of the pinned key passed
    /// here and was then refused by every device it reached.
    /// </summary>
    private static byte[] RequireBytes(int index, string? text, int length, string field)
        => IsCanonical(text, length, out var bytes)
            ? bytes
            : throw BadGrant(index, field, $"must be {length} bytes, as base64url text.");

    private static bool IsCanonical(string? text, int length, out byte[] bytes)
    {
        try
        {
            bytes = B64.FromUrl(text ?? "");
        }
        catch (CryptographicException)
        {
            bytes = [];
        }

        return bytes.Length == length && string.Equals(B64.Url(bytes), text, StringComparison.Ordinal);
    }

    // Numbered from 1 and naming the field, so the sender can tell which grant of a batch was refused, and why.
    private static GatewayFault BadGrant(int index, string field, string why)
        => GatewayFault.BadGrant($"Grant {index + 1}: '{field}' {why}");

    private sealed record CheckedGrant(KeyGrant Grant, byte[] SigningPublic);

    // ── invitations ─────────────────────────────────────────────────────────

    /// <summary>
    /// How long an invitation can be answered. Its link carries the pairing secret, and a link left in a
    /// chat history or a screenshot must stop admitting anybody soon after the person who made it is done.
    /// </summary>
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A trusted browser invites another device. The id is the browser's own making: it is in the link the
    /// browser shows, beside the pairing secret this gateway never sees. Returns when the invitation expires.
    /// </summary>
    public Task<DateTimeOffset> CreateInviteAsync(DeviceAccess device, string? id, CancellationToken ct)
    {
        var inviteId = RequireInviteId(id, "id");

        return db.InTransactionAsync(async (connection, transaction) =>
        {
            // The lock the count is taken under, and the refusal for an account deleted since its session was
            // checked, as in RegisterAsync.
            if (!await connection.ExistsAsync(transaction,
                    "SELECT 1 FROM users WHERE id = @owner FOR UPDATE", ("@owner", device.OwnerId)))
            {
                throw GatewayFault.Unauthenticated();
            }

            await RequireLiveDeviceAsync(connection, transaction, device.OwnerId, device.DeviceId);

            return await InsertInviteAsync(
                connection, transaction, device.OwnerId, inviteId, null, device.DeviceId, Audit.User(device.OwnerId));
        }, ct);
    }

    /// <summary>A computer invites another device of its owner's ("Add a device" on the desktop).</summary>
    public Task<DateTimeOffset> CreateInviteAsync(HostAccess host, string? id, CancellationToken ct)
    {
        var inviteId = RequireInviteId(id, "id");

        return db.InTransactionAsync(async (connection, transaction) =>
        {
            await AuthorizeComputerAsync(connection, transaction, host, lockAccount: true);

            return await InsertInviteAsync(
                connection, transaction, host.OwnerId, inviteId, host.HostId, null, Audit.Host(host.HostId));
        }, ct);
    }

    /// <summary>
    /// The account's row is locked by the caller, so the count and the insert are one step: counted without
    /// the lock, two invitations that both read "one place left" were both made. The count comes after the
    /// lock for the reason given in <see cref="RegisterAsync"/>.
    /// </summary>
    private async Task<DateTimeOffset> InsertInviteAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId, string inviteId,
        string? byHost, string? byDevice, string actor)
    {
        var now = clock.GetUtcNow();

        // Used and expired invitations are not open. An expired one can no longer admit anybody, and nothing
        // deletes it yet: counted, it would hold a place for nothing until something did.
        var open = await connection.ReadOneAsync(transaction,
            "SELECT COUNT(*) FROM invites WHERE owner_id = @owner AND consumed_at IS NULL AND expires_at > @now",
            reader => reader.GetInt64(0), ("@owner", ownerId), ("@now", now));

        if (open >= limits.OpenInvitesPerUser)
        {
            throw GatewayFault.InviteLimit(limits.OpenInvitesPerUser);
        }

        var expires = now + InviteLifetime;

        try
        {
            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO invites (id, owner_id, created_by_host, created_by_device, created_at, expires_at)
                VALUES (@id, @owner, @host, @device, @now, @expires)
                """,
                ("@id", inviteId), ("@owner", ownerId), ("@host", (object?)byHost ?? DBNull.Value),
                ("@device", (object?)byDevice ?? DBNull.Value), ("@now", now), ("@expires", expires));
        }
        catch (MySqlException error) when (error.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            // The caller makes the id, and within one account it names one invitation: replacing the one there
            // would hand its answer to another inviter. The key starts with the owner, so this is only ever the
            // person's own invitation - another person's under the same id is not in the way and not touched.
            throw GatewayFault.Conflict("You already have an invitation with that id; make another id.");
        }

        await Audit.WriteAsync(connection, transaction, now, ownerId, actor, Audit.InviteCreated, inviteId);
        return expires;
    }

    /// <summary>
    /// A new device answers an invitation of its person's. The invitation's row is read under a lock and
    /// marked used in the same transaction as the enrollment is stored, so two devices answering at once
    /// queue there and the second is told it is used: read without the lock, both found it open and both
    /// enrolled.
    ///
    /// <para>The MAC is stored as sent, after a check of its shape only. The gateway has no pair key and
    /// cannot verify it; the inviter does, and that check is what stops a gateway putting another key in
    /// the new device's place.</para>
    /// </summary>
    public async Task EnrollAsync(
        UserAccess user, string? inviteId, string? deviceId, string? mac, CancellationToken ct)
    {
        var invite = RequireInviteId(inviteId, "inviteId");

        if (!IsId(deviceId))
        {
            throw GatewayFault.BadRequest(
                "'deviceId' must be an id as this service issues them: 32 lowercase hex characters.");
        }

        // One spelling of the bytes, as for a grant's MAC: a MAC that could never verify is refused here,
        // where the new device sees the refusal, not later by an inviter nobody is watching.
        if (!IsCanonical(mac, HmacLength, out _))
        {
            throw GatewayFault.BadRequest($"'mac' must be {HmacLength} bytes, as base64url text.");
        }

        // The answering device is the person's own and not removed - whichever device makes the call. Checked
        // again inside the transaction below; this one records the visit and refuses early, without a lock.
        await RequireAsync(user, deviceId, ct);

        await db.InTransactionAsync(async (connection, transaction) =>
        {
            // The primary key starts with the owner, so this lock cannot reach another person's invitation.
            var row = await connection.ReadOneAsync(transaction,
                """
                SELECT consumed_at IS NOT NULL AS used, expires_at FROM invites
                WHERE owner_id = @owner AND id = @id
                FOR UPDATE
                """,
                reader => ((bool Used, DateTimeOffset ExpiresAt)?)(reader.GetBoolean("used"), reader.Utc("expires_at")),
                ("@owner", user.UserId), ("@id", invite));

            if (row is null)
            {
                throw GatewayFault.UnknownInvite();
            }

            if (row.Value.Used)
            {
                throw GatewayFault.InviteUsed();
            }

            var now = clock.GetUtcNow();

            if (row.Value.ExpiresAt <= now)
            {
                throw GatewayFault.InviteExpired();
            }

            await RequireLiveDeviceAsync(connection, transaction, user.UserId, deviceId);

            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO enrollments (invite_id, owner_id, device_id, mac, created_at)
                VALUES (@id, @owner, @device, @mac, @now)
                """,
                ("@id", invite), ("@owner", user.UserId), ("@device", deviceId), ("@mac", mac), ("@now", now));

            await connection.ExecuteAsync(transaction,
                "UPDATE invites SET consumed_at = @now WHERE owner_id = @owner AND id = @id",
                ("@now", now), ("@owner", user.UserId), ("@id", invite));

            await Audit.WriteAsync(
                connection, transaction, now, user.UserId, Audit.User(user.UserId), Audit.DeviceEnrolled, deviceId);
        }, ct);
    }

    /// <summary>
    /// The answer to an invitation a browser of this person's made, or null while there is none. Any of the
    /// person's live devices may read it: the pairing secret, not the reader, is what protects it - the MAC
    /// is worth nothing to anybody without the secret. An invitation a computer made is the computer's to
    /// read, and is refused here like a missing one.
    /// </summary>
    public async Task<EnrollmentView?> ReadEnrollmentAsync(DeviceAccess device, string inviteId, CancellationToken ct)
    {
        // Not an id at all is not anybody's invitation. Checked because the id column ignores trailing
        // spaces: "id " would have found the invitation.
        if (!IsId(inviteId))
        {
            throw GatewayFault.UnknownInvite();
        }

        await using var connection = await db.OpenAsync(ct);

        // An answer from a device removed since is left out, as for a computer below: the inviter would
        // otherwise grant keys to a browser the person has already cut off.
        var rows = await connection.ReadAllAsync(null,
            """
            SELECT e.device_id, d.public_key, d.label, e.mac
            FROM invites i
            LEFT JOIN enrollments e ON e.owner_id = i.owner_id AND e.invite_id = i.id
            LEFT JOIN devices d ON d.owner_id = e.owner_id AND d.id = e.device_id AND d.revoked_at IS NULL
            WHERE i.owner_id = @owner AND i.id = @id AND i.created_by_device IS NOT NULL
            """,
            reader => reader.IsDBNull(reader.GetOrdinal("public_key")) ? null : Enrollment(reader, inviteId),
            ("@owner", device.OwnerId), ("@id", inviteId));

        return rows.Count == 0 ? throw GatewayFault.UnknownInvite() : rows[0];
    }

    /// <summary>
    /// The answers to this computer's own invitations that it has not handled yet, oldest first. Another
    /// computer's are not among them, nor a browser's: each is for whoever holds its invitation's secret.
    /// </summary>
    public Task<IReadOnlyList<EnrollmentView>> EnrollmentsAsync(HostAccess host, CancellationToken ct)
        => db.InTransactionAsync<IReadOnlyList<EnrollmentView>>(async (connection, transaction) =>
        {
            await AuthorizeComputerAsync(connection, transaction, host, lockAccount: false);

            return await connection.ReadAllAsync(transaction,
                """
                SELECT e.invite_id, e.device_id, d.public_key, d.label, e.mac
                FROM invites i
                JOIN enrollments e ON e.owner_id = i.owner_id AND e.invite_id = i.id
                JOIN devices d ON d.owner_id = e.owner_id AND d.id = e.device_id
                WHERE i.owner_id = @owner AND i.created_by_host = @host
                  AND e.answered_at IS NULL AND d.revoked_at IS NULL
                ORDER BY e.created_at, e.invite_id
                """,
                reader => Enrollment(reader, reader.GetString("invite_id")),
                ("@owner", host.OwnerId), ("@host", host.HostId));
        }, ct);

    /// <summary>
    /// The computer has handled the answer to its invitation, so it is not handed over again. Saying so
    /// twice succeeds: the computer repeats it after a reply it did not receive, and a refusal would have it
    /// repeat it for ever. Another computer's invitation, another person's, and one nobody has answered are
    /// all refused like a missing one.
    /// </summary>
    public Task AnsweredInviteAsync(HostAccess host, string? inviteId, CancellationToken ct)
    {
        if (!IsId(inviteId))
        {
            throw GatewayFault.UnknownInvite();
        }

        return db.InTransactionAsync(async (connection, transaction) =>
        {
            await AuthorizeComputerAsync(connection, transaction, host, lockAccount: false);

            // Both keys start with the owner, so neither read can reach another person's row. The enrollment is
            // locked only once the invitation is known to be this computer's: another computer of the same
            // person's naming it would otherwise hold it while being refused.
            var own = await connection.ExistsAsync(transaction,
                """
                SELECT 1 FROM invites
                WHERE owner_id = @owner AND id = @id AND created_by_host = @host
                """,
                ("@owner", host.OwnerId), ("@id", inviteId), ("@host", host.HostId));

            var answered = own
                ? await connection.ReadOneAsync(transaction,
                    "SELECT answered_at IS NOT NULL FROM enrollments WHERE owner_id = @owner AND invite_id = @id FOR UPDATE",
                    reader => (bool?)reader.GetBoolean(0), ("@owner", host.OwnerId), ("@id", inviteId))
                : null;

            if (answered is null)
            {
                throw GatewayFault.UnknownInvite();
            }

            if (!answered.Value)
            {
                await connection.ExecuteAsync(transaction,
                    "UPDATE enrollments SET answered_at = @now WHERE owner_id = @owner AND invite_id = @id",
                    ("@now", clock.GetUtcNow()), ("@owner", host.OwnerId), ("@id", inviteId));
            }
        }, ct);
    }

    /// <summary>
    /// The device an invitation call is made for is the person's own and not removed, read under a shared
    /// lock in the call's transaction. A revocation holds the device's row until it commits, so a call that
    /// arrives meanwhile waits and then finds it removed. Checked only before the transaction, a device revoked
    /// in between was enrolled - or made an invitation - after it had been cut off.
    /// </summary>
    private static async Task RequireLiveDeviceAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId, string deviceId)
    {
        // FORCE INDEX, as in RevokeAsync: through the primary key another person's row would be locked first.
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
            throw GatewayFault.DeviceRevoked();
        }
    }

    private static EnrollmentView Enrollment(MySqlDataReader reader, string inviteId) => new(
        inviteId,
        reader.GetString("device_id"),
        B64.Url((byte[])reader["public_key"]),
        reader.GetString("label"),
        reader.GetString("mac"));

    /// <summary>
    /// The account is active and the computer live, read inside the call's transaction as on every call of a
    /// computer's (see <see cref="HostService"/>). The account's row is locked for update when the call counts
    /// the account's invitations under it, and shared otherwise; nothing here writes the computer's row, so
    /// it is shared. Taken shared and then wanted for update, the account's row would deadlock two
    /// invitations made at once, each holding it shared and waiting for the other to let go.
    /// </summary>
    private static async Task AuthorizeComputerAsync(
        MySqlConnection connection, MySqlTransaction transaction, HostAccess host, bool lockAccount)
    {
        var status = await connection.ReadOneAsync(transaction,
            lockAccount
                ? "SELECT status FROM users WHERE id = @owner FOR UPDATE"
                : "SELECT status FROM users WHERE id = @owner FOR SHARE",
            reader => reader.GetString("status"), ("@owner", host.OwnerId));

        // Not there at all is a computer whose account is gone, and its row with it: the unknown-computer
        // refusal below.
        if (status is not null && status != "Active")
        {
            throw GatewayFault.AccountDisabled();
        }

        var revoked = await connection.ReadOneAsync(transaction,
            """
            SELECT revoked FROM hosts FORCE INDEX (ux_hosts_owner)
            WHERE owner_id = @owner AND id = @host
            FOR SHARE
            """,
            reader => (bool?)reader.GetBoolean("revoked"), ("@owner", host.OwnerId), ("@host", host.HostId));

        if (revoked is null)
        {
            throw GatewayFault.UnknownHost();
        }

        if (revoked.Value)
        {
            throw GatewayFault.HostRevoked();
        }
    }

    /// <summary>
    /// An invitation's id as its maker sends it: 32 lowercase hex characters, as <see cref="Ids.New"/> makes
    /// them. Anything else is refused rather than stored: the id column ignores trailing spaces, so "id "
    /// would name the same invitation as "id".
    /// </summary>
    private static string RequireInviteId(string? id, string field)
        => IsId(id)
            ? id
            : throw GatewayFault.BadRequest($"'{field}' must be an invitation id: 32 lowercase hex characters.");

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

    /// <summary>
    /// Marks an endpoint of the person's API that a browser calls before it has a device to name: registering
    /// one, and signing out. Every other one names its device and is refused when that device was removed.
    /// </summary>
    public sealed class NotRequired
    {
        public static readonly NotRequired Instance = new();

        private NotRequired() { }
    }
}
