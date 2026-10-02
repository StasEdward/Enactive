namespace Enactive.Remote.Host;

using System.Globalization;
using System.Security.Cryptography;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Secrets;
using Microsoft.Data.Sqlite;

/// <summary>A browser device this computer grants its keys to. Revoked devices stay listed, with the time.</summary>
public sealed record TrustedDevice(
    string DeviceId, byte[] PublicKey, string Label, string AddedBy, DateTimeOffset AddedAt, DateTimeOffset? RevokedAt);

/// <summary>An invitation this computer opened: the pairing secret it put in the link, and when it stops counting.</summary>
public sealed record PendingInvite(string Id, byte[] Secret, DateTimeOffset ExpiresAt);

/// <summary>A grant made and not yet delivered to the gateway.</summary>
public sealed record PendingGrant(string Id, KeyGrant Grant);

/// <summary>
/// The grants this computer still owes the gateway, as the delivery loop sees them: read, and
/// discarded once delivered or refused for good. An interface so the loop's handling of a refusal
/// can be proven without a key store protected for a Windows user.
/// </summary>
public interface IGrantOutbox
{
    /// <summary>The grants still owed, oldest first.</summary>
    IReadOnlyList<PendingGrant> PendingGrants();

    /// <summary>Delivered, or no longer wanted.</summary>
    void DiscardGrant(string id);
}

/// <summary>
/// The computer's remote keys could not be read, or are not all there. The service says so and does
/// not connect; nothing has been replaced.
/// </summary>
public sealed class HostKeysUnreadableException(string message) : Exception(message);

/// <summary>
/// This computer's keys for remote access, kept in remote.db and protected for this Windows user: an
/// epoch key per epoch, the signing key, the devices it trusts, the invitations it has open and the
/// grants it still owes the gateway.
///
/// <para><b>Nothing is ever invented to cover a key that cannot be read.</b> A store copied from
/// another Windows user, or restored onto a reinstalled profile, holds ciphertext DPAPI will not
/// open for this account. Making new keys there would look like a working computer, while every
/// trusted device could read nothing it sent from then on and nobody would be told why. So the
/// constructor throws <see cref="HostKeysUnreadableException"/>, and the person connects the
/// computer again with a new code.</para>
///
/// <para><b>All epoch keys are kept.</b> A command may arrive sealed under an epoch the computer has
/// since rotated away from, and events already delivered stay sealed under theirs, so rotation adds
/// a key and never removes one.</para>
///
/// <para><b>The signing key is made with epoch 1 and never rotated.</b> Every device pins it at its
/// first verified grant and checks every rotation grant against it, so a new one would make every
/// later rotation grant look forged.</para>
///
/// <para><b>One HostKeyStore per remote.db.</b> The epoch keys are read once and held in memory;
/// a second instance over the same file would rotate from its own idea of the highest epoch and
/// never see the other's keys. The service makes one at startup and shares it.</para>
/// </summary>
public sealed class HostKeyStore : IHostKeys, IGrantOutbox, IDisposable
{
    /// <summary>What the service tells the person when the keys cannot be read on this account.</summary>
    public const string UnreadableMessage =
        "This computer's remote keys cannot be read on this account - connect it again with a new connection code";

    /// <summary>
    /// How long an invitation link counts. Long enough to open the link on a phone, short enough
    /// that a link left in a chat or a screenshot is useless by the time anyone else finds it.
    /// </summary>
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(10);

    private const int SecretLength = 32;

    private readonly HostStore _store;
    private readonly TimeProvider _clock;
    private readonly ECDsa _signer;
    private readonly byte[] _signingPublic;

    // Replaced whole on rotation rather than added to, so a reader on another thread sees the old
    // list or the new one and never one being changed under it.
    private volatile HostKey[] _keys;

    public HostKeyStore(HostStore store, string hostId, TimeProvider? clock = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
        HostId = hostId;

        (_keys, _signer) = store.Locked(Load);
        _signingPublic = P256.SigningPublicRaw(_signer);
    }

    public string HostId { get; }

    /// <summary>The highest epoch: everything new is sealed under it.</summary>
    public HostKey Current => _keys[^1];

    /// <summary>Every epoch this computer holds, oldest first.</summary>
    public IReadOnlyList<HostKey> All => _keys;

    public HostKey? Epoch(uint epoch) => Array.Find(_keys, k => k.Epoch == epoch);

    /// <summary>The signing key that authenticates rotation grants. Disposed with the store.</summary>
    public ECDsa Signer => _signer;

    /// <summary>The raw public point of <see cref="Signer"/>, as every grant carries it.</summary>
    public byte[] SigningPublic => (byte[])_signingPublic.Clone();

    /// <summary>
    /// Makes the next epoch's key, writes it down, and seals with it from now on.
    ///
    /// <para>Written before it is used: a key sealed with and then lost in a crash would leave
    /// whatever it sealed unreadable on every device, including this one.</para>
    /// </summary>
    public HostKey Rotate() => _store.Locked(connection =>
    {
        var next = HostKey.Create(_keys[^1].Epoch + 1);
        Execute(connection, null, "INSERT INTO host_keys (epoch, secret) VALUES ($epoch, $secret)",
            ("$epoch", (long)next.Epoch), ("$secret", Protect(next.Secret.Span)));
        _keys = [.. _keys, next];
        return next;
    });

    // ── trusted devices ─────────────────────────────────────────────────────

    /// <summary>Every device this computer has trusted, revoked ones included.</summary>
    public IReadOnlyList<TrustedDevice> Trusted => _store.Locked(connection =>
    {
        var devices = new List<TrustedDevice>();
        using var statement = connection.CreateCommand();
        statement.CommandText = """
            SELECT device_id, public_key, label, added_by, added_at, revoked_at
            FROM trusted_devices ORDER BY added_at, device_id
            """;
        using var reader = statement.ExecuteReader();
        while (reader.Read())
        {
            devices.Add(new TrustedDevice(
                reader.GetString(0), (byte[])reader.GetValue(1), reader.GetString(2), reader.GetString(3),
                Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : Parse(reader.GetString(5))));
        }
        return (IReadOnlyList<TrustedDevice>)devices;
    });

    /// <summary>The devices that are trusted now: the ones a rotation grants its new key to.</summary>
    public IReadOnlyList<TrustedDevice> Live => [.. Trusted.Where(d => d.RevokedAt is null)];

    /// <summary>
    /// Trusts a device this computer has never trusted. Trusting a live device again with the key it
    /// already has changes nothing; the first decision, with its time and author, stands.
    ///
    /// <para><b>A live device is never rebound to another key.</b> An endorsement can be sealed by
    /// any device that holds an epoch key, a revoked or stolen one included. If this replaced the
    /// key of a live id, that device could put its own key under a victim's name and receive the
    /// next epoch's key, as the victim, at the next rotation. So another key for a known id is
    /// refused, and so is a revoked id: bringing a device back is <see cref="Retrust"/>, which only
    /// the admission paths call.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The id is known with another key, or revoked.</exception>
    public void Trust(TrustedDevice device)
    {
        RequirePoint(device.DeviceId, device.PublicKey);

        _store.Locked(connection =>
        {
            if (StoredDevice(connection, device.DeviceId) is { } known)
            {
                if (known.Revoked)
                    throw new InvalidOperationException(
                        $"Device {device.DeviceId} was revoked; trusting it again is a new admission, not an endorsement.");
                if (!CryptographicOperations.FixedTimeEquals(known.PublicKey, device.PublicKey))
                    throw new InvalidOperationException(
                        $"Device {device.DeviceId} is already trusted with another key; a device id is never rebound to a new key.");
                return 0;
            }

            return Execute(connection, null,
                """
                INSERT INTO trusted_devices (device_id, public_key, label, added_by, added_at, revoked_at)
                VALUES ($id, $key, $label, $by, $at, NULL)
                """,
                ("$id", device.DeviceId), ("$key", device.PublicKey), ("$label", device.Label),
                ("$by", device.AddedBy), ("$at", Format(device.AddedAt)));
        });
    }

    /// <summary>
    /// Trusts a revoked device again, with the key it had. Only the desktop app's own admission and
    /// an answered invitation call this - both rest on something the person did on this computer or
    /// on an out-of-band secret, which an endorsement sealed under an epoch key does not.
    ///
    /// <para>It is a new trust decision, so who made it and when are recorded anew. A different
    /// key is refused: a device with a new key is a new device, and gets a new id.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The id is unknown, or was trusted with another key.</exception>
    public void Retrust(string deviceId, byte[] publicKey, string addedBy)
    {
        RequirePoint(deviceId, publicKey);

        _store.Locked(connection =>
        {
            var known = StoredDevice(connection, deviceId)
                ?? throw new InvalidOperationException($"Device {deviceId} was never trusted here, so there is nothing to trust again.");
            if (!CryptographicOperations.FixedTimeEquals(known.PublicKey, publicKey))
                throw new InvalidOperationException($"Device {deviceId} was trusted with another key; a device id is never rebound to a new key.");
            if (!known.Revoked) return 0;

            return Execute(connection, null,
                "UPDATE trusted_devices SET revoked_at = NULL, added_by = $by, added_at = $now WHERE device_id = $id",
                ("$by", addedBy), ("$now", Format(_clock.GetUtcNow())), ("$id", deviceId));
        });
    }

    /// <summary>
    /// Marks a device revoked and drops every grant still queued for it, in one step. The row
    /// stays, so the list can say who was trusted and when that ended; the time of the first
    /// revocation is kept if it is revoked twice.
    ///
    /// <para>The queued grants go with it because a grant made just before the revocation and
    /// delivered just after would hand the device a key after the person took its trust away.</para>
    /// </summary>
    public void Distrust(string deviceId) => _store.Locked(connection =>
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction,
            "UPDATE trusted_devices SET revoked_at = $now WHERE device_id = $id AND revoked_at IS NULL",
            ("$now", Format(_clock.GetUtcNow())), ("$id", deviceId));
        Execute(connection, transaction, "DELETE FROM pending_grants WHERE device_id = $id", ("$id", deviceId));
        transaction.Commit();
        return 0;
    });

    // Refused here rather than when the first grant to it is made: a key that is not a P-256 point
    // cannot be granted to, and a rotation that met it would fail for every device after it.
    private static void RequirePoint(string deviceId, byte[] publicKey)
    {
        try
        {
            using (P256.ImportPublic(publicKey)) { }
        }
        catch (CryptographicException ex)
        {
            throw new ArgumentException($"Device {deviceId} has no valid P-256 public key.", nameof(publicKey), ex);
        }
    }

    private static (byte[] PublicKey, bool Revoked)? StoredDevice(SqliteConnection connection, string deviceId)
    {
        using var statement = connection.CreateCommand();
        statement.CommandText = "SELECT public_key, revoked_at FROM trusted_devices WHERE device_id = $id";
        statement.Parameters.AddWithValue("$id", deviceId);
        using var reader = statement.ExecuteReader();
        return reader.Read() ? ((byte[])reader.GetValue(0), !reader.IsDBNull(1)) : null;
    }

    // ── invitations ─────────────────────────────────────────────────────────

    /// <summary>
    /// Opens an invitation: a new id and pairing secret, counting for <see cref="InviteLifetime"/>.
    ///
    /// <para>The secret is stored protected like the keys. Whoever holds it can enroll a device that
    /// this computer then grants every key to, so in the clear it would be a key to everything for
    /// the next ten minutes.</para>
    /// </summary>
    public PendingInvite CreateInvite()
    {
        var invite = new PendingInvite(
            Guid.NewGuid().ToString("N"), RandomNumberGenerator.GetBytes(SecretLength),
            _clock.GetUtcNow() + InviteLifetime);

        _store.Locked(connection => Execute(connection, null,
            "INSERT INTO pending_invites (id, secret, expires_at) VALUES ($id, $secret, $expires)",
            ("$id", invite.Id), ("$secret", Protect(invite.Secret)), ("$expires", Format(invite.ExpiresAt))));

        return invite;
    }

    /// <summary>
    /// An open invitation, or null when there is none by that id or it has expired.
    ///
    /// <para>Expired invitations are deleted here, lazily: nothing else has a reason to look at
    /// them, and an expired one must never be answered, whoever asks.</para>
    /// </summary>
    public PendingInvite? Invite(string id) => _store.Locked(connection =>
    {
        var now = _clock.GetUtcNow();

        // Every expires_at is written by Format, in UTC with the same width, so comparing the
        // text compares the times.
        Execute(connection, null, "DELETE FROM pending_invites WHERE expires_at <= $now", ("$now", Format(now)));

        using var statement = connection.CreateCommand();
        statement.CommandText = "SELECT secret, expires_at FROM pending_invites WHERE id = $id";
        statement.Parameters.AddWithValue("$id", id);
        using var reader = statement.ExecuteReader();
        if (!reader.Read()) return null;

        // An invitation whose secret this account cannot read cannot be answered: no enrollment
        // could be checked against it. It is as good as missing, and lives ten minutes at most.
        var secret = Unprotect(reader.GetString(0));
        return secret is null ? null : new PendingInvite(id, secret, Parse(reader.GetString(1)));
    });

    /// <summary>Closes an invitation: it was answered, or withdrawn. Single use either way.</summary>
    public void ForgetInvite(string id)
        => _store.Locked(connection => Execute(connection, null, "DELETE FROM pending_invites WHERE id = $id", ("$id", id)));

    // ── grants owed to the gateway ──────────────────────────────────────────

    /// <summary>
    /// Queues a grant for delivery, to a device this computer trusts now.
    ///
    /// <para>Keyed by computer, device and epoch, so a grant made again - after a restart, or for a
    /// device enrolled twice - replaces the one still waiting instead of sending both.</para>
    /// </summary>
    public void EnqueueGrant(KeyGrant grant)
    {
        // A grant for another computer queued here would be delivered under this computer's name.
        if (grant.HostId != HostId)
            throw new ArgumentException($"A grant for {grant.HostId} cannot be queued by {HostId}.", nameof(grant));

        _store.Locked(connection =>
        {
            // Checked under the same lock as Distrust, so a grant cannot slip in between a
            // revocation and the dropping of that device's queue.
            if (StoredDevice(connection, grant.DeviceId) is not { Revoked: false })
                throw new InvalidOperationException(
                    $"Device {grant.DeviceId} is not trusted by this computer, so nothing is granted to it.");

            return Execute(connection, null,
                """
                INSERT INTO pending_grants (id, device_id, json, created_at) VALUES ($id, $device, $json, $now)
                ON CONFLICT (id) DO UPDATE SET json = excluded.json, created_at = excluded.created_at
                """,
                ("$id", GrantId(grant)), ("$device", grant.DeviceId), ("$json", RemoteJson.Serialize(grant)),
                ("$now", Format(_clock.GetUtcNow())));
        });
    }

    /// <summary>The grants still owed, oldest first.</summary>
    public IReadOnlyList<PendingGrant> PendingGrants() => _store.Locked(connection =>
    {
        var grants = new List<PendingGrant>();
        using var statement = connection.CreateCommand();
        // rowid breaks ties between grants queued within one tick of the clock.
        statement.CommandText = "SELECT id, json FROM pending_grants ORDER BY created_at, rowid";
        using var reader = statement.ExecuteReader();
        while (reader.Read())
        {
            grants.Add(new PendingGrant(reader.GetString(0), RemoteJson.Deserialize<KeyGrant>(reader.GetString(1))));
        }
        return (IReadOnlyList<PendingGrant>)grants;
    });

    /// <summary>Delivered, or no longer wanted.</summary>
    public void DiscardGrant(string id)
        => _store.Locked(connection => Execute(connection, null, "DELETE FROM pending_grants WHERE id = $id", ("$id", id)));

    public void Dispose() => _signer.Dispose();

    // ── the identity as a whole ─────────────────────────────────────────────

    /// <summary>
    /// The tables that make up this computer's remote identity. Listed once, so that clearing the
    /// identity cannot forget one: a trusted device or a queued grant left behind would be granted the
    /// NEW identity's keys, by a computer that no longer has any reason to trust it.
    /// </summary>
    private static readonly string[] IdentityTables =
        ["pending_grants", "pending_invites", "trusted_devices", "host_signing", "host_keys"];

    /// <summary>
    /// Whether remote.db holds any key of this computer's, readable or not. Asked before a key store is
    /// made, because making one is what creates the keys.
    /// </summary>
    public static bool HasKeys(HostStore store) => store.Locked(connection =>
    {
        using var statement = connection.CreateCommand();
        statement.CommandText =
            "SELECT EXISTS (SELECT 1 FROM host_keys) OR EXISTS (SELECT 1 FROM host_signing)";
        return Convert.ToInt64(statement.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    });

    /// <summary>
    /// Forgets this computer's remote identity: every key, every trusted device, every invitation and
    /// every grant still owed, in one transaction. The next key store made over this file starts again
    /// at epoch 1 with a new signing key.
    ///
    /// <para>Static, because the case it exists for includes keys that cannot be read - and a key
    /// store cannot be made over those, which is the whole of what <see cref="HostKeysUnreadableException"/>
    /// says. Nobody may hold a key store over the file while this runs: it would go on sealing with
    /// keys that are no longer written anywhere.</para>
    /// </summary>
    public static void Reset(HostStore store) => store.Locked(connection =>
    {
        using var transaction = connection.BeginTransaction();
        foreach (var table in IdentityTables)
        {
            Execute(connection, transaction, $"DELETE FROM {table}");
        }
        transaction.Commit();
        return 0;
    });

    // ── loading ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads every key, or makes epoch 1 and the signing key on first use - both in one transaction,
    /// so a crash cannot leave one without the other.
    /// </summary>
    private (HostKey[] Keys, ECDsa Signer) Load(SqliteConnection connection)
    {
        var stored = new List<(long Epoch, string Secret)>();
        using (var statement = connection.CreateCommand())
        {
            statement.CommandText = "SELECT epoch, secret FROM host_keys ORDER BY epoch";
            using var reader = statement.ExecuteReader();
            while (reader.Read()) stored.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        string? signing;
        using (var statement = connection.CreateCommand())
        {
            statement.CommandText = "SELECT private_key FROM host_signing WHERE id = 1";
            signing = statement.ExecuteScalar() as string;
        }

        if (stored.Count == 0 && signing is null) return Create(connection);

        // Half a store is not a first use. Making the missing half would give the computer a
        // signing key no device pinned, or epoch keys no device holds - the same silent breakage as
        // replacing an unreadable key.
        if (signing is null)
            throw new HostKeysUnreadableException(
                "This computer's remote keys are incomplete: its signing key is missing - connect it again with a new connection code");
        if (stored.Count == 0)
            throw new HostKeysUnreadableException(
                "This computer's remote keys are incomplete: it has a signing key and no epoch keys - connect it again with a new connection code");

        var keys = stored.Select(row =>
        {
            var secret = Unprotect(row.Secret);
            if (secret is null || secret.Length != SecretLength || row.Epoch is < 1 or > uint.MaxValue)
            {
                if (secret is not null) CryptographicOperations.ZeroMemory(secret);
                throw new HostKeysUnreadableException(UnreadableMessage);
            }
            // Not cleared after this: the HostKey keeps this very array as its secret.
            return HostKey.From((uint)row.Epoch, secret);
        }).ToArray();

        return (keys, ImportSigner(signing));
    }

    private static (HostKey[] Keys, ECDsa Signer) Create(SqliteConnection connection)
    {
        var first = HostKey.Create(1);
        var signer = P256.GenerateSigning();
        var exported = signer.ExportECPrivateKey();
        try
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "INSERT INTO host_keys (epoch, secret) VALUES (1, $secret)",
                ("$secret", Protect(first.Secret.Span)));
            Execute(connection, transaction, "INSERT INTO host_signing (id, private_key) VALUES (1, $key)",
                ("$key", Protect(exported)));
            transaction.Commit();
        }
        catch
        {
            // The constructor never returns, so nothing else would dispose the key it made.
            signer.Dispose();
            throw;
        }
        finally
        {
            // The private key belongs in DPAPI's output and in the ECDsa object, not in a stray array.
            CryptographicOperations.ZeroMemory(exported);
        }

        return ([first], signer);
    }

    private static ECDsa ImportSigner(string stored)
    {
        var raw = Unprotect(stored) ?? throw new HostKeysUnreadableException(UnreadableMessage);
        var signer = ECDsa.Create();
        try
        {
            signer.ImportECPrivateKey(raw, out _);
            // Grants are verified as P-256 on every device; a key on another curve - including
            // another 256-bit one, such as brainpoolP256r1 - would sign grants none of them can check.
            var curve = signer.ExportParameters(includePrivateParameters: false).Curve;
            if (!curve.IsNamed || curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new CryptographicException("The signing key is not P-256.");
            return signer;
        }
        catch (CryptographicException)
        {
            signer.Dispose();
            throw new HostKeysUnreadableException(UnreadableMessage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private static string GrantId(KeyGrant grant)
        => $"{grant.HostId}:{grant.DeviceId}:{grant.Epoch.ToString(CultureInfo.InvariantCulture)}";

    private static string Protect(ReadOnlySpan<byte> secret) => Secret.Protect(Convert.ToBase64String(secret));

    /// <summary>
    /// The bytes, or null when this account cannot read them. Text that is not protected at all is
    /// refused too: this store never writes a secret in the clear, so one found that way was put
    /// there by something else.
    /// </summary>
    private static byte[]? Unprotect(string stored)
    {
        if (!Secret.IsProtected(stored)) return null;
        var clear = Secret.Unprotect(stored);
        if (clear.Length == 0) return null;
        try
        {
            return Convert.FromBase64String(clear);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Round-trippable, and in UTC so that times compare as text.</summary>
    private static string Format(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string text)
        => DateTimeOffset.ParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static int Execute(
        SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var statement = connection.CreateCommand();
        statement.CommandText = sql;
        statement.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            statement.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return statement.ExecuteNonQuery();
    }
}
