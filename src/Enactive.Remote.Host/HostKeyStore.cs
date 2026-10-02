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
/// </summary>
public sealed class HostKeyStore : IHostKeys, IDisposable
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
    /// Trusts a device, or trusts it again after a revocation.
    ///
    /// <para>Trusting a revoked device again is a new decision, so it takes the new record whole -
    /// key, label, who added it and when - rather than only clearing the revocation and keeping a
    /// record that describes the old decision.</para>
    /// </summary>
    public void Trust(TrustedDevice device)
    {
        // Refused here rather than when the first grant to it is made: a key that is not a P-256
        // point cannot be granted to, and a rotation that met it would fail for every device after it.
        try
        {
            using (P256.ImportPublic(device.PublicKey)) { }
        }
        catch (CryptographicException ex)
        {
            throw new ArgumentException($"Device {device.DeviceId} has no valid P-256 public key.", nameof(device), ex);
        }

        _store.Locked(connection => Execute(connection, null,
            """
            INSERT INTO trusted_devices (device_id, public_key, label, added_by, added_at, revoked_at)
            VALUES ($id, $key, $label, $by, $at, NULL)
            ON CONFLICT (device_id) DO UPDATE SET
              public_key = excluded.public_key, label = excluded.label, added_by = excluded.added_by,
              added_at = excluded.added_at, revoked_at = NULL
            """,
            ("$id", device.DeviceId), ("$key", device.PublicKey), ("$label", device.Label),
            ("$by", device.AddedBy), ("$at", Format(device.AddedAt))));
    }

    /// <summary>
    /// Marks a device revoked. The row stays, so the list can say who was trusted and when that
    /// ended; the time of the first revocation is kept if it is revoked twice.
    /// </summary>
    public void Distrust(string deviceId)
        => _store.Locked(connection => Execute(connection, null,
            "UPDATE trusted_devices SET revoked_at = $now WHERE device_id = $id AND revoked_at IS NULL",
            ("$now", Format(_clock.GetUtcNow())), ("$id", deviceId)));

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
    /// Queues a grant for delivery.
    ///
    /// <para>Keyed by computer, device and epoch, so a grant made again - after a restart, or for a
    /// device enrolled twice - replaces the one still waiting instead of sending both.</para>
    /// </summary>
    public void EnqueueGrant(KeyGrant grant)
    {
        // A grant for another computer queued here would be delivered under this computer's name.
        if (grant.HostId != HostId)
            throw new ArgumentException($"A grant for {grant.HostId} cannot be queued by {HostId}.", nameof(grant));

        _store.Locked(connection => Execute(connection, null,
            """
            INSERT INTO pending_grants (id, json, created_at) VALUES ($id, $json, $now)
            ON CONFLICT (id) DO UPDATE SET json = excluded.json, created_at = excluded.created_at
            """,
            ("$id", GrantId(grant)), ("$json", RemoteJson.Serialize(grant)), ("$now", Format(_clock.GetUtcNow()))));
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
                throw new HostKeysUnreadableException(UnreadableMessage);
            return HostKey.From((uint)row.Epoch, secret);
        }).ToArray();

        return (keys, ImportSigner(signing));
    }

    private static (HostKey[] Keys, ECDsa Signer) Create(SqliteConnection connection)
    {
        var first = HostKey.Create(1);
        var signer = P256.GenerateSigning();

        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT INTO host_keys (epoch, secret) VALUES (1, $secret)",
            ("$secret", Protect(first.Secret.Span)));
        Execute(connection, transaction, "INSERT INTO host_signing (id, private_key) VALUES (1, $key)",
            ("$key", Protect(signer.ExportECPrivateKey())));
        transaction.Commit();

        return ([first], signer);
    }

    private static ECDsa ImportSigner(string stored)
    {
        var raw = Unprotect(stored) ?? throw new HostKeysUnreadableException(UnreadableMessage);
        var signer = ECDsa.Create();
        try
        {
            signer.ImportECPrivateKey(raw, out _);
            // Grants are verified as P-256 on every device; a key on another curve would sign
            // grants none of them can check.
            if (signer.KeySize != 256) throw new CryptographicException("The signing key is not P-256.");
            return signer;
        }
        catch (CryptographicException)
        {
            signer.Dispose();
            throw new HostKeysUnreadableException(UnreadableMessage);
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
