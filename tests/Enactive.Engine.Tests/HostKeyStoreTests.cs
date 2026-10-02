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
    [InlineData("host_keys", "secret", "epoch = 1")]
    [InlineData("host_signing", "private_key", "id = 1")]
    public void A_key_this_windows_user_cannot_read_is_reported_not_replaced(string table, string column, string where)
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("remote.db");
        using (var store = new HostStore(path))
        using (new HostKeyStore(store, HostId)) { }

        // A ciphertext this account made with other entropy is what another program's (or another
        // build's) secret looks like: DPAPI refuses it, exactly as it refuses another user's.
        var foreign = "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(
            RandomNumberGenerator.GetBytes(32), Encoding.UTF8.GetBytes("not-enactive"), Secret.Scope));
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

        // Trusting it again is a new decision, not a quiet undo of the revocation.
        clock.Advance(TimeSpan.FromMinutes(5));
        keys.Trust(Device("phone", phone, clock.GetUtcNow()));
        var again = Assert.Single(keys.Trusted, d => d.DeviceId == "phone");
        Assert.Null(again.RevokedAt);
        Assert.Equal(clock.GetUtcNow(), again.AddedAt);
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

    // ── helpers ─────────────────────────────────────────────────────────────

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
