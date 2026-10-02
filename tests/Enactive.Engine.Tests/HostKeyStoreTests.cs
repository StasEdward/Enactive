namespace Enactive.Engine.Tests;

using System.Security.Cryptography;
using System.Text;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Host;
using Enactive.Secrets;
using Microsoft.Data.Sqlite;
using Xunit;

/// <summary>
/// The computer's keys at rest: its epoch keys, its signing key, the devices it trusts, the invitations
/// it has open and the grants it still owes, all in remote.db and protected for this Windows user.
///
/// <para>What matters most here is what happens when the keys cannot be read. Making new ones would
/// look like a working computer, while every trusted device could read nothing it sent from then on
/// and nobody would be told why. So an unreadable store is reported, and nothing is replaced.</para>
/// </summary>
public sealed class HostKeyStoreTests
{
    private const string HostId = "host-1";

    [WindowsFact]
    public void Keys_survive_a_restart()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("remote.db");

        byte[] first, second, signing;
        using (var store = new HostStore(path))
        using (var keys = new HostKeyStore(store, HostId))
        {
            first = keys.Current.Secret.ToArray();
            second = keys.Rotate().Secret.ToArray();
            signing = keys.SigningPublic;
        }

        using (var store = new HostStore(path))
        using (var keys = new HostKeyStore(store, HostId))
        {
            Assert.Equal(2u, keys.Current.Epoch);
            Assert.Equal(first, keys.Epoch(1)!.Secret.ToArray());
            Assert.Equal(second, keys.Epoch(2)!.Secret.ToArray());
            Assert.Equal(signing, keys.SigningPublic);
            Assert.Equal(signing, P256.SigningPublicRaw(keys.Signer));
        }
    }

    [WindowsFact]
    public void First_use_makes_epoch_one_and_the_signing_key_together()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("remote.db");

        using (var store = new HostStore(path))
        using (var keys = new HostKeyStore(store, HostId))
        {
            Assert.Equal(HostId, keys.HostId);
            Assert.Equal(1u, keys.Current.Epoch);
            Assert.Single(keys.All);
            Assert.Equal(65, keys.SigningPublic.Length);
        }

        Assert.Equal(1L, Count(path, "host_keys"));
        Assert.Equal(1L, Count(path, "host_signing"));
    }

    [WindowsFact]
    public void Rotation_adds_an_epoch_and_keeps_the_old_ones()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, HostId);
        var signing = keys.SigningPublic;
        var first = keys.Current;

        var second = keys.Rotate();
        var third = keys.Rotate();

        Assert.Equal(2u, second.Epoch);
        Assert.Equal(3u, third.Epoch);
        Assert.Same(third, keys.Current);
        Assert.Equal([1u, 2u, 3u], keys.All.Select(k => k.Epoch));
        Assert.Equal(first.Secret.ToArray(), keys.Epoch(1)!.Secret.ToArray());
        Assert.NotEqual(first.Secret.ToArray(), second.Secret.ToArray());
        Assert.Null(keys.Epoch(4));
        // The signing key is what a device pinned at its first grant; rotating it would make every
        // rotation grant after it unverifiable.
        Assert.Equal(signing, keys.SigningPublic);
    }

    [WindowsTheory]
    [InlineData("host_keys", "secret", "epoch = 1", "foreign")]
    [InlineData("host_signing", "private_key", "id = 1", "foreign")]
    [InlineData("host_keys", "secret", "epoch = 1", "clear")]
    [InlineData("host_signing", "private_key", "id = 1", "clear")]
    [InlineData("host_signing", "private_key", "id = 1", "other-curve")]
    public void A_key_this_windows_user_cannot_read_is_reported_not_replaced(
        string table, string column, string where, string kind)
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("remote.db");
        using (var store = new HostStore(path))
        using (new HostKeyStore(store, HostId)) { }

        var foreign = kind switch
        {
            // A ciphertext this account made with other entropy is what another program's (or another
            // build's) secret looks like: DPAPI refuses it, exactly as it refuses another user's.
            "foreign" => "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(
                RandomNumberGenerator.GetBytes(32), Encoding.UTF8.GetBytes("not-enactive"), Secret.Scope)),
            // Secret.Unprotect hands back unprefixed text as it is; the store never writes a key that
            // way, so one found like that was put there by something else.
            "clear" => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            // Readable, and 256 bits, but not P-256: no device could check a grant it signed.
            _ => OtherCurveSigningKey(),
        };
        Execute(path, $"UPDATE {table} SET {column} = $value WHERE {where}", foreign);

        using (var store = new HostStore(path))
        {
            var refused = Assert.Throws<HostKeysUnreadableException>(() => new HostKeyStore(store, HostId));
            Assert.Contains("cannot be read on this account", refused.Message, StringComparison.Ordinal);
        }

        Assert.Equal(foreign, Read(path, $"SELECT {column} FROM {table} WHERE {where}"));
        Assert.Equal(1L, Count(path, "host_keys"));
        Assert.Equal(1L, Count(path, "host_signing"));
    }

    [WindowsFact]
    public void A_store_with_keys_but_no_signing_key_is_reported()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("remote.db");
        using (var store = new HostStore(path))
        using (new HostKeyStore(store, HostId)) { }

        Execute(path, "DELETE FROM host_signing");

        using (var store = new HostStore(path))
        {
            var refused = Assert.Throws<HostKeysUnreadableException>(() => new HostKeyStore(store, HostId));
            Assert.Contains("signing key", refused.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0L, Count(path, "host_signing"));
    }

    [WindowsFact]
    public void A_store_with_a_signing_key_but_no_epoch_keys_is_reported()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("remote.db");
        using (var store = new HostStore(path))
        using (new HostKeyStore(store, HostId)) { }

        Execute(path, "DELETE FROM host_keys");

        using (var store = new HostStore(path))
        {
            var refused = Assert.Throws<HostKeysUnreadableException>(() => new HostKeyStore(store, HostId));
            Assert.Contains("no epoch keys", refused.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0L, Count(path, "host_keys"));
        Assert.Equal(1L, Count(path, "host_signing"));
    }

    [WindowsFact]
    public void Distrusting_a_device_keeps_it_in_the_list_as_revoked()
    {
        using var fx = new EngineFixture();
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, HostId, clock);
        using var phone = P256.Generate();
        using var laptop = P256.Generate();

        keys.Trust(Device("phone", phone, clock.GetUtcNow()));
        keys.Trust(Device("laptop", laptop, clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(5));
        keys.Distrust("phone");

        var revoked = Assert.Single(keys.Trusted, d => d.DeviceId == "phone");
        Assert.Equal(clock.GetUtcNow(), revoked.RevokedAt);
        Assert.Equal(P256.PublicRaw(phone), revoked.PublicKey);
        Assert.Equal(["laptop"], keys.Live.Select(d => d.DeviceId));
    }

    // An endorsement can be sealed by any device that holds an epoch key, a revoked one included. If
    // Trust rebound a live id to a new key, that device could swap a victim's key for its own and
    // receive the next epoch's key under the victim's name at the next rotation.
    [WindowsFact]
    public void A_live_device_is_not_rebound_to_another_key()
    {
        using var fx = new EngineFixture();
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, HostId, clock);
        using var phone = P256.Generate();
        using var attacker = P256.Generate();
        var addedAt = clock.GetUtcNow();
        keys.Trust(Device("phone", phone, addedAt));

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Throws<InvalidOperationException>(() => keys.Trust(Device("phone", attacker, clock.GetUtcNow())));

        var kept = Assert.Single(keys.Trusted);
        Assert.Equal(P256.PublicRaw(phone), kept.PublicKey);
    }

    [WindowsFact]
    public void Trusting_a_live_device_again_with_its_own_key_changes_nothing()
    {
        using var fx = new EngineFixture();
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, HostId, clock);
        using var phone = P256.Generate();
        var addedAt = clock.GetUtcNow();
        keys.Trust(Device("phone", phone, addedAt));

        clock.Advance(TimeSpan.FromMinutes(5));
        keys.Trust(new TrustedDevice("phone", P256.PublicRaw(phone), "renamed", "someone-else", clock.GetUtcNow(), null));

        var kept = Assert.Single(keys.Trusted);
        Assert.Equal(addedAt, kept.AddedAt);
        Assert.Equal("test", kept.AddedBy);
        Assert.Null(kept.RevokedAt);
    }

    [WindowsFact]
    public void Trust_does_not_reactivate_a_revoked_device()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, HostId);
        using var phone = P256.Generate();
        keys.Trust(Device("phone", phone, DateTimeOffset.UtcNow));
        keys.Distrust("phone");

        Assert.Throws<InvalidOperationException>(() => keys.Trust(Device("phone", phone, DateTimeOffset.UtcNow)));
        Assert.NotNull(Assert.Single(keys.Trusted).RevokedAt);
    }

    [WindowsFact]
    public void Retrust_reactivates_a_revoked_device_only_with_its_own_key()
    {
        using var fx = new EngineFixture();
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, HostId, clock);
        using var phone = P256.Generate();
        using var other = P256.Generate();
        keys.Trust(Device("phone", phone, clock.GetUtcNow()));
        keys.Distrust("phone");

        Assert.Throws<InvalidOperationException>(() => keys.Retrust("phone", P256.PublicRaw(other), "desktop"));
        Assert.NotNull(Assert.Single(keys.Trusted).RevokedAt);
        Assert.Throws<InvalidOperationException>(() => keys.Retrust("never-seen", P256.PublicRaw(other), "desktop"));

        clock.Advance(TimeSpan.FromMinutes(5));
        keys.Retrust("phone", P256.PublicRaw(phone), "desktop");

        // A new trust decision: who made it and when are the new ones.
        var again = Assert.Single(keys.Live);
        Assert.Equal("desktop", again.AddedBy);
        Assert.Equal(clock.GetUtcNow(), again.AddedAt);
        Assert.Equal(P256.PublicRaw(phone), again.PublicKey);
    }

    [WindowsFact]
    public void A_device_key_that_is_not_a_p256_point_is_refused()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, HostId);

        Assert.Throws<ArgumentException>(() => keys.Trust(
            new TrustedDevice("odd", new byte[65], "odd", "test", DateTimeOffset.UtcNow, null)));
        Assert.Empty(keys.Trusted);
    }

    [WindowsFact]
    public void An_expired_invite_is_not_returned()
    {
        using var fx = new EngineFixture();
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        var path = fx.PathOf("remote.db");
        using var store = new HostStore(path);
        using var keys = new HostKeyStore(store, HostId, clock);

        var invite = keys.CreateInvite();
        Assert.Matches("^[0-9a-f]{32}$", invite.Id);
        Assert.Equal(32, invite.Secret.Length);
        Assert.Equal(clock.GetUtcNow().AddMinutes(10), invite.ExpiresAt);

        clock.Advance(TimeSpan.FromMinutes(9));
        var found = keys.Invite(invite.Id);
        Assert.NotNull(found);
        Assert.Equal(invite.Secret, found.Secret);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(keys.Invite(invite.Id));
        Assert.Equal(0L, Count(path, "pending_invites"));

        var forgotten = keys.CreateInvite();
        keys.ForgetInvite(forgotten.Id);
        Assert.Null(keys.Invite(forgotten.Id));
        Assert.Null(keys.Invite("no-such-invite"));
    }

    [WindowsFact]
    public void An_invite_secret_is_not_stored_in_clear()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("remote.db");
        byte[] secret;
        using (var store = new HostStore(path))
        using (var keys = new HostKeyStore(store, HostId))
        {
            secret = keys.CreateInvite().Secret;
        }

        var bytes = File.ReadAllBytes(path);
        if (File.Exists(path + "-wal")) bytes = [.. bytes, .. File.ReadAllBytes(path + "-wal")];

        Assert.False(Contains(bytes, Encoding.ASCII.GetBytes(Convert.ToBase64String(secret))));
        Assert.False(Contains(bytes, Encoding.ASCII.GetBytes(B64.Url(secret))));
        Assert.False(Contains(bytes, secret));
    }

    [WindowsFact]
    public void A_re_made_grant_replaces_the_pending_one()
    {
        using var fx = new EngineFixture();
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, HostId, clock);
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        keys.Trust(Device("phone", phone, clock.GetUtcNow()));
        keys.Trust(Device("laptop", laptop, clock.GetUtcNow()));

        keys.EnqueueGrant(Grants.CreateSigned(HostId, "phone", P256.PublicRaw(phone), keys.Current, keys.Signer));
        clock.Advance(TimeSpan.FromSeconds(1));
        keys.EnqueueGrant(Grants.CreateSigned(HostId, "laptop", P256.PublicRaw(laptop), keys.Current, keys.Signer));
        clock.Advance(TimeSpan.FromSeconds(1));
        var remade = Grants.CreateSigned(HostId, "phone", P256.PublicRaw(phone), keys.Current, keys.Signer);
        keys.EnqueueGrant(remade);

        var pending = keys.PendingGrants();
        Assert.Equal(["host-1:laptop:1", "host-1:phone:1"], pending.Select(p => p.Id));
        Assert.Equal(remade, pending[1].Grant);

        keys.DiscardGrant("host-1:laptop:1");
        Assert.Equal(["host-1:phone:1"], keys.PendingGrants().Select(p => p.Id));
    }

    // A grant queued for a device that is revoked, or was never trusted, would hand it a key the
    // moment the delivery loop next runs.
    [WindowsFact]
    public void A_grant_for_a_device_that_is_not_live_is_refused()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, HostId);
        using var phone = P256.Generate();
        using var stranger = P256.Generate();
        keys.Trust(Device("phone", phone, DateTimeOffset.UtcNow));
        keys.Distrust("phone");

        Assert.Throws<InvalidOperationException>(() => keys.EnqueueGrant(
            Grants.CreateSigned(HostId, "phone", P256.PublicRaw(phone), keys.Current, keys.Signer)));
        Assert.Throws<InvalidOperationException>(() => keys.EnqueueGrant(
            Grants.CreateSigned(HostId, "stranger", P256.PublicRaw(stranger), keys.Current, keys.Signer)));
        Assert.Empty(keys.PendingGrants());
    }

    // Revocation followed by delivery of a grant made just before it would give the revoked device
    // a key after the person took its trust away.
    [WindowsFact]
    public void Distrusting_a_device_drops_its_queued_grants_and_no_others()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, HostId);
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        keys.Trust(Device("phone", phone, DateTimeOffset.UtcNow));
        keys.Trust(Device("laptop", laptop, DateTimeOffset.UtcNow));
        keys.Rotate();
        foreach (var key in keys.All)
        {
            keys.EnqueueGrant(Grants.CreateSigned(HostId, "phone", P256.PublicRaw(phone), key, keys.Signer));
            keys.EnqueueGrant(Grants.CreateSigned(HostId, "laptop", P256.PublicRaw(laptop), key, keys.Signer));
        }
        Assert.Equal(4, keys.PendingGrants().Count);

        keys.Distrust("phone");

        Assert.Equal(["host-1:laptop:1", "host-1:laptop:2"], keys.PendingGrants().Select(p => p.Id).Order());
    }

    /// <summary>
    /// A removal is one step: the device distrusted with its queued grants dropped, the next epoch written,
    /// a grant of it signed for every device that remains, and - when the desktop removed it - the gateway
    /// owed word of it. The new epoch is kept across a restart.
    /// </summary>
    [WindowsFact]
    public void Revoke_and_rotate_distrusts_makes_the_next_epoch_and_grants_the_rest_in_one_step()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("remote.db");
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        using (var store = new HostStore(path))
        using (var keys = new HostKeyStore(store, HostId))
        {
            keys.Trust(Device("phone", phone, DateTimeOffset.UtcNow));
            keys.Trust(Device("laptop", laptop, DateTimeOffset.UtcNow));
            keys.EnqueueGrant(Grants.CreateSigned(HostId, "laptop", P256.PublicRaw(laptop), keys.Current, keys.Signer));

            var (next, remaining) = keys.RevokeAndRotate("laptop", tellGateway: true);

            Assert.Equal(2u, next.Epoch);
            Assert.Equal(2u, keys.Current.Epoch);
            Assert.Equal(["phone"], remaining.Select(d => d.DeviceId));
            Assert.NotNull(Assert.Single(keys.Trusted, d => d.DeviceId == "laptop").RevokedAt);
            var grant = Assert.Single(keys.PendingGrants()).Grant;
            Assert.Equal(("phone", 2u, Grants.AuthByHost), (grant.DeviceId, grant.Epoch, grant.AuthBy));
            Assert.Equal(next.Secret.ToArray(), Grants.Open(grant, phone, default, keys.SigningPublic).Key.Secret.ToArray());
            Assert.Equal(["laptop"], keys.OwedRevocations());

            keys.RevokeAndRotate("phone", tellGateway: false);
            Assert.Equal(["laptop"], keys.OwedRevocations());
        }

        using (var store = new HostStore(path))
        using (var keys = new HostKeyStore(store, HostId))
        {
            Assert.Equal(3u, keys.Current.Epoch);
        }
    }

    /// <summary>
    /// A removal that fails part of the way - here, a grant that cannot be made for the second device that
    /// remains - changes nothing: the device is still trusted, the epoch is the one it was, and no grant
    /// and no word owed to the gateway is left behind. Made as three steps, the device was left shown as
    /// removed while it still held the current key and read everything new, and a second removal was a
    /// no-op because it was already marked removed.
    /// </summary>
    [WindowsFact]
    public void A_revoke_and_rotate_that_fails_part_way_changes_nothing()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("remote.db");
        using var store = new HostStore(path);
        using var keys = new HostKeyStore(store, HostId);
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        keys.Trust(Device("phone", phone, DateTimeOffset.UtcNow));
        keys.Trust(Device("laptop", laptop, DateTimeOffset.UtcNow));
        TrustUngrantable(path, "broken");

        Assert.ThrowsAny<CryptographicException>(() => keys.RevokeAndRotate("laptop", tellGateway: true));

        Assert.Equal(1u, keys.Current.Epoch);
        Assert.Equal(1L, Count(path, "host_keys"));
        Assert.Null(Assert.Single(keys.Trusted, d => d.DeviceId == "laptop").RevokedAt);
        Assert.Empty(keys.PendingGrants());
        Assert.Empty(keys.OwedRevocations());

        keys.Distrust("broken");
        keys.RevokeAndRotate("laptop", tellGateway: true);

        Assert.Equal(2u, keys.Current.Epoch);
        Assert.Equal(["phone"], keys.Live.Select(d => d.DeviceId));
        Assert.Equal(["host-1:phone:2"], keys.PendingGrants().Select(p => p.Id));
    }

    /// <summary>A removal names a device trusted now; anything else is refused and changes nothing.</summary>
    [WindowsFact]
    public void Revoke_and_rotate_refuses_a_device_that_is_not_trusted_now()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, HostId);
        using var phone = P256.Generate();
        keys.Trust(Device("phone", phone, DateTimeOffset.UtcNow));
        keys.Distrust("phone");

        Assert.Throws<InvalidOperationException>(() => keys.RevokeAndRotate("phone", tellGateway: true));
        Assert.Throws<InvalidOperationException>(() => keys.RevokeAndRotate("stranger", tellGateway: true));

        Assert.Equal(1u, keys.Current.Epoch);
        Assert.Empty(keys.OwedRevocations());
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A trusted device no grant can be made for - its key is off the curve - written past the store's own
    /// check, and listed after every other device.
    /// </summary>
    internal static void TrustUngrantable(string path, string deviceId)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trusted_devices (device_id, public_key, label, added_by, added_at, revoked_at)
            VALUES ($id, $key, 'Broken', 'test', '9999-12-31T00:00:00.0000000+00:00', NULL)
            """;
        command.Parameters.AddWithValue("$id", deviceId);
        command.Parameters.AddWithValue("$key", (byte[])[0x04, .. Enumerable.Repeat((byte)0x01, 64)]);
        command.ExecuteNonQuery();
    }

    private static string OtherCurveSigningKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1);
        return Secret.Protect(Convert.ToBase64String(key.ExportECPrivateKey()));
    }

    private static TrustedDevice Device(string id, ECDiffieHellman key, DateTimeOffset at)
        => new(id, P256.PublicRaw(key), id + " browser", "test", at, null);

    private static bool Contains(byte[] haystack, byte[] needle)
        => haystack.AsSpan().IndexOf(needle) >= 0;

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(string path, string sql, string? value = null)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (value is not null) command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static object? Read(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static long Count(string path, string table) => (long)Read(path, $"SELECT COUNT(*) FROM {table}")!;

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
