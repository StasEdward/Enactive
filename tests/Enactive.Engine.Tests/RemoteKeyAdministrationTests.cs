namespace Enactive.Engine.Tests;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Enactive.App.Ui;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Host;
using Enactive.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

/// <summary>
/// Connecting this computer with a connection code (spec §5.2): the one code a person carries from
/// the browser to the desktop, which says where the gateway is, which computer this is, the
/// credential it connects with, and the browser that is to be trusted first.
///
/// <para>The code also carries the pairing secret, and that is the part with the strictest rule: it
/// authenticates the first grant and is then forgotten. Kept anywhere, it would let whoever read it
/// later make a grant that browser accepts as this computer's.</para>
/// </summary>
public sealed class RemoteKeyAdministrationTests
{
    private static readonly Uri Gateway = new("https://remote.example.test");

    // ── reading the code ────────────────────────────────────────────────────

    /// <summary>
    /// A code that cannot be used is explained, in the sentence the parser wrote for the person who
    /// pasted it - which says what is wrong and what to do - and not as a bare failure to connect.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("https://remote.example.test")]
    [InlineData("enactive-connect:")]
    [InlineData("enactive-connect:eyJ2IjoyfQ")]
    [InlineData("enactive-connect:not base64!")]
    public void A_broken_code_is_explained_under_the_box(string text)
    {
        var expected = Assert.Throws<PairingCodeException>(() => ConnectionCode.Parse(text)).Message;

        Assert.Null(Pairing.TryRead(text, out var problem));
        Assert.Equal(expected, problem);
    }

    /// <summary>The explanation never repeats the code's token: it is shown on screen, under the box.</summary>
    [Fact]
    public void The_explanation_of_a_broken_code_does_not_show_its_token()
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        using var device = P256.Generate();
        var cutShort = new ConnectionCode(Gateway, "host-1", token, "device-1", P256.PublicRaw(device), new byte[16]).Format();

        Assert.Null(Pairing.TryRead(cutShort, out var problem));
        Assert.NotEmpty(problem);
        Assert.DoesNotContain(token, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_good_code_is_read_without_a_problem()
    {
        var (code, device) = NewCode();
        using (device)
        {
            var read = Pairing.TryRead("  " + code.Format() + "\r\n", out var problem);

            Assert.NotNull(read);
            Assert.Equal(string.Empty, problem);
            Assert.Equal(code.HostId, read.HostId);
        }
    }

    // ── applying it ─────────────────────────────────────────────────────────

    /// <summary>
    /// The code's browser is trusted, as the code's, and is owed a grant of every key this computer
    /// holds - authenticated with the pair key, so it is the one grant a browser with nothing pinned
    /// yet can check, and carrying the signing key it pins from then on.
    /// </summary>
    [WindowsFact]
    public void A_code_trusts_its_device_and_queues_a_paired_grant_of_every_key()
    {
        using var fx = new EngineFixture();
        var (code, device) = NewCode();
        using var _ = device;
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, code.HostId);
        keys.Rotate();

        var outcome = Pairing.Apply(code, keys);

        Assert.True(outcome.Paired);
        Assert.Equal(2, outcome.GrantsQueued);
        var trusted = Assert.Single(keys.Live);
        Assert.Equal(code.DeviceId, trusted.DeviceId);
        Assert.Equal(code.DevicePublic, trusted.PublicKey);
        Assert.Equal(Pairing.AddedBy, trusted.AddedBy);

        var pending = keys.PendingGrants();
        Assert.Equal([1u, 2u], pending.Select(p => p.Grant.Epoch).Order());
        foreach (var grant in pending.Select(p => p.Grant))
        {
            Assert.Equal(Grants.AuthByPairing("connect"), grant.AuthBy);
            var (opened, signing) = Grants.Open(grant, device, code.PairKey, pinnedHostSigningPublic: null);
            Assert.Equal(keys.Epoch(grant.Epoch)!.Secret.ToArray(), opened.Secret.ToArray());
            Assert.Equal(keys.SigningPublic, signing);
        }
    }

    /// <summary>
    /// A browser this computer revoked can be connected again by a new code made in it - the code is
    /// the person at both ends - but only with the key it had. A different key under that id is
    /// another device claiming its name.
    /// </summary>
    [WindowsFact]
    public void A_revoked_device_is_trusted_again_by_a_code_only_with_its_own_key()
    {
        using var fx = new EngineFixture();
        var (code, device) = NewCode();
        using var _ = device;
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, code.HostId);
        Pairing.Apply(code, keys);
        keys.Distrust(code.DeviceId);

        using var other = P256.Generate();
        var impostor = code with { DevicePublic = P256.PublicRaw(other) };
        Assert.Throws<InvalidOperationException>(() => Pairing.Apply(impostor, keys));
        Assert.Empty(keys.Live);

        Pairing.Apply(code, keys);

        Assert.Equal(code.DeviceId, Assert.Single(keys.Live).DeviceId);
        Assert.Single(keys.PendingGrants());
    }

    /// <summary>
    /// THE rule of the code: the pairing secret is used once, to authenticate the first grant, and is
    /// written nowhere - not in remote.db, not in its write-ahead log, not in settings.json. Searched
    /// for as base64url (how the code carries it), as base64, and as raw bytes.
    /// </summary>
    [WindowsFact]
    public async Task The_pairing_secret_is_not_stored()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settingsFile = fx.PathOf("settings.json");
        var (code, device) = NewCode();
        using var _ = device;
        var settings = new AppSettings();

        var outcome = await RemoteAccessService.ConnectWithCodeAsync(code, settings.RemoteAccess, database, NeverAsked);

        Assert.True(outcome.Paired);
        Assert.True(settings.Save(settingsFile), settings.LastSaveError);

        var stored = File.ReadAllBytes(settingsFile).Concat(File.ReadAllBytes(database));
        if (File.Exists(database + "-wal")) stored = stored.Concat(File.ReadAllBytes(database + "-wal"));
        var bytes = stored.ToArray();

        Assert.False(Contains(bytes, Encoding.ASCII.GetBytes(B64.Url(code.PairingSecret))));
        Assert.False(Contains(bytes, Encoding.ASCII.GetBytes(Convert.ToBase64String(code.PairingSecret))));
        Assert.False(Contains(bytes, code.PairingSecret));
        Assert.False(Contains(bytes, Encoding.ASCII.GetBytes(code.Token)));

        // And what it was for did happen: the settings name the computer, and the grant is waiting.
        Assert.Equal(code.HostId, settings.RemoteAccess.HostId);
        Assert.Equal("https://remote.example.test", settings.RemoteAccess.GatewayUrl);
        Assert.Equal(code.Token, settings.RemoteAccess.Token);
        Assert.True(settings.RemoteAccess.Enabled);
        using var store = new HostStore(database);
        using var keys = new HostKeyStore(store, code.HostId);
        Assert.Single(keys.PendingGrants());
    }

    /// <summary>
    /// A code for another computer, on a computer that already has keys, replaces its identity: every
    /// device that could read it will not read anything new. So it is asked first, and a no changes
    /// nothing - not the keys, not the settings.
    /// </summary>
    [WindowsFact]
    public async Task A_code_for_another_computer_asks_before_replacing_keys()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (first, firstDevice) = NewCode("host-a", "device-a");
        var (second, secondDevice) = NewCode("host-b", "device-b");
        using var _ = firstDevice;
        using var __ = secondDevice;
        await RemoteAccessService.ConnectWithCodeAsync(first, settings, database, NeverAsked);
        var signingBefore = SigningPublic(database, "host-a");
        var asked = 0;

        var declined = await RemoteAccessService.ConnectWithCodeAsync(second, settings, database, () =>
        {
            asked++;
            return Task.FromResult(false);
        });

        Assert.Equal(1, asked);
        Assert.False(declined.Paired);
        Assert.Equal("host-a", settings.HostId);
        Assert.Equal(first.Token, settings.Token);
        Assert.Equal(signingBefore, SigningPublic(database, "host-a"));

        var replaced = await RemoteAccessService.ConnectWithCodeAsync(second, settings, database, () =>
        {
            asked++;
            return Task.FromResult(true);
        });

        Assert.Equal(2, asked);
        Assert.True(replaced.Paired);
        Assert.True(replaced.Replaced);
        Assert.Equal("host-b", settings.HostId);
        Assert.Equal(second.Token, settings.Token);

        using var store = new HostStore(database);
        using var keys = new HostKeyStore(store, "host-b");
        Assert.NotEqual(signingBefore, keys.SigningPublic);
        Assert.Equal("device-b", Assert.Single(keys.Trusted).DeviceId);
        Assert.Equal("host-b", Assert.Single(keys.PendingGrants()).Grant.HostId);
    }

    /// <summary>
    /// A second code for the same computer - another browser registering it again - adds its device
    /// to the keys the computer has. Nothing is replaced, so nothing is asked.
    /// </summary>
    [WindowsFact]
    public async Task A_code_for_the_same_computer_adds_its_device_without_asking()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (first, firstDevice) = NewCode("host-a", "device-a");
        var (second, secondDevice) = NewCode("host-a", "device-b");
        using var _ = firstDevice;
        using var __ = secondDevice;
        await RemoteAccessService.ConnectWithCodeAsync(first, settings, database, NeverAsked);
        var signingBefore = SigningPublic(database, "host-a");

        var outcome = await RemoteAccessService.ConnectWithCodeAsync(second, settings, database, NeverAsked);

        Assert.True(outcome.Paired);
        Assert.False(outcome.Replaced);
        Assert.Equal(signingBefore, SigningPublic(database, "host-a"));
        using var store = new HostStore(database);
        using var keys = new HostKeyStore(store, "host-a");
        Assert.Equal(["device-a", "device-b"], keys.Live.Select(d => d.DeviceId).Order());
    }

    /// <summary>
    /// Keys this account cannot read are replaced only with the person's yes, even for the same
    /// computer: a code is how the unreadable-keys message tells them to recover, and recovering is
    /// a new identity that their other devices will not read.
    /// </summary>
    [WindowsFact]
    public async Task Keys_this_account_cannot_read_are_replaced_only_when_the_person_says_so()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode("host-a", "device-a");
        using var _ = device;
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        MakeUnreadable(database);
        var asked = 0;

        var outcome = await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, () =>
        {
            asked++;
            return Task.FromResult(true);
        });

        Assert.Equal(1, asked);
        Assert.True(outcome.Replaced);
        using var store = new HostStore(database);
        using var keys = new HostKeyStore(store, "host-a");
        Assert.Single(keys.Trusted);
    }

    // ── the service ─────────────────────────────────────────────────────────

    /// <summary>
    /// Hello is the first thing said, and a gateway of another protocol stops the service with the
    /// gateway's own sentence. Going on would be every later call refused for a different-looking
    /// reason, and a reconnect loop around a mismatch only a person updating something can fix.
    /// </summary>
    [Fact]
    public async Task Another_protocol_stops_the_service_with_the_gateways_words()
    {
        const string words = "This computer and the service speak different versions - update Enactive.";
        using var fx = new EngineFixture();
        var gateway = new FakeGateway
        {
            HelloRefusal = new GatewayRefusedException(FaultCode.ProtocolMismatch, words)
        };
        var connects = 0;
        await using var service = Service(fx, new FixedHostKeys(), _ =>
        {
            connects++;
            return Task.FromResult<IGatewayConnection>(gateway);
        });

        service.Start();

        await StoppedAsync(service);
        Assert.Contains(words, service.Status, StringComparison.Ordinal);
        Assert.Equal(["Hello"], gateway.Calls);
        Assert.Equal(1, connects);
    }

    /// <summary>
    /// A credential the gateway refuses at the door - revoked, or its account disabled - reaches the
    /// Host as a bare 401. Treated as a dropped connection it was dialled again for ever, with nothing
    /// saying why; now the service stops and says what happened in a sentence.
    /// </summary>
    [Fact]
    public async Task A_refused_credential_stops_the_service_and_says_why()
    {
        using var fx = new EngineFixture();
        var connects = 0;
        await using var service = Service(fx, new FixedHostKeys(), _ =>
        {
            connects++;
            return Task.FromException<IGatewayConnection>(new GatewayCredentialRefusedException());
        });

        service.Start();

        await StoppedAsync(service);
        Assert.Contains(GatewayCredentialRefusedException.Sentence, service.Status, StringComparison.Ordinal);
        Assert.Equal(1, connects);
    }

    /// <summary>
    /// A computer whose keys this account cannot read says so and does not connect: connecting would
    /// publish workspaces sealed under keys no device holds.
    /// </summary>
    [WindowsFact]
    public async Task Keys_this_account_cannot_read_are_reported_and_nothing_connects()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode();
        using var _ = device;
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        MakeUnreadable(database);
        var connects = 0;

        await using var service = new RemoteAccessService(settings, keys: null,
            _ => throw new InvalidOperationException("No composition expected"), () => [], fx.Decisions, database,
            connect: _ =>
            {
                connects++;
                return Task.FromResult<IGatewayConnection>(new FakeGateway());
            });

        service.Start();

        Assert.Equal(HostKeyStore.UnreadableMessage, service.Status);
        Assert.Equal(0, connects);
    }

    /// <summary>
    /// A computer id in the settings with no keys behind it - remote.db deleted, or replaced - is said,
    /// and no keys are made. Made silently, they would connect a computer no device can read.
    /// </summary>
    [Fact]
    public async Task A_computer_id_with_no_keys_behind_it_is_reported_and_no_keys_are_made()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var connects = 0;
        var settings = new RemoteAccessSettings
        {
            Enabled = true, GatewayUrl = Gateway.ToString(), HostId = "host-1", Token = "token"
        };

        await using (var service = new RemoteAccessService(settings, keys: null,
            _ => throw new InvalidOperationException("No composition expected"), () => [], fx.Decisions, database,
            connect: _ =>
            {
                connects++;
                return Task.FromResult<IGatewayConnection>(new FakeGateway());
            }))
        {
            service.Start();

            Assert.Contains("keys are missing", service.Status, StringComparison.Ordinal);
            Assert.Equal(0, connects);
        }

        using var store = new HostStore(database);
        Assert.False(HostKeyStore.HasKeys(store));
    }

    /// <summary>Test connection on a computer no code has connected says what to do, without dialling anything.</summary>
    [Fact]
    public async Task Test_connection_before_any_code_says_to_connect_one()
    {
        using var fx = new EngineFixture();
        await using var service = new RemoteAccessService(new RemoteAccessSettings(), keys: null,
            _ => throw new InvalidOperationException("No composition expected"), () => [], fx.Decisions,
            fx.PathOf("remote.db"),
            connect: _ => throw new Xunit.Sdk.XunitException("Nothing should have been dialled."));

        Assert.Equal(RemoteAccessService.NeedsPairing, await service.CheckAsync(CancellationToken.None));
    }

    /// <summary>
    /// The whole desktop path: a code applied, then the service started from the settings it left.
    /// It reads the keys the code made, says hello, sends the browser its grant, and only then syncs -
    /// the grant first, because the browser can read nothing this computer publishes without it.
    /// </summary>
    [WindowsFact]
    public async Task A_connected_computer_says_hello_and_sends_the_grant_before_it_syncs()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode();
        using var _ = device;
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        var gateway = new FakeGateway();

        await using (var service = new RemoteAccessService(settings, keys: null,
            _ => throw new InvalidOperationException("No composition expected"), () => [], fx.Decisions, database,
            connect: _ => Task.FromResult<IGatewayConnection>(gateway)))
        {
            service.Start();
            await Until(() => gateway.Calls.Contains("Sync"));
        }

        Assert.Equal(["Hello", "PublishGrants", "Sync"], gateway.Calls.Take(3));
        var grant = Assert.Single(Assert.Single(gateway.GrantCalls));
        Assert.Equal(code.DeviceId, grant.DeviceId);
        Grants.Open(grant, device, code.PairKey, pinnedHostSigningPublic: null);
    }

    // ── the key store ───────────────────────────────────────────────────────

    /// <summary>Reset leaves nothing of the old identity behind: no key, no device, no invitation, no grant.</summary>
    [WindowsFact]
    public void Reset_clears_every_table_of_the_key_store()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var (code, device) = NewCode();
        using var _ = device;
        using (var store = new HostStore(database))
        {
            using (var keys = new HostKeyStore(store, code.HostId))
            {
                Pairing.Apply(code, keys);
                keys.CreateInvite();
            }

            Assert.True(HostKeyStore.HasKeys(store));
            HostKeyStore.Reset(store);
            Assert.False(HostKeyStore.HasKeys(store));
        }

        foreach (var table in new[] { "host_keys", "host_signing", "trusted_devices", "pending_invites", "pending_grants" })
        {
            Assert.Equal(0L, Count(database, table));
        }
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private static Task<bool> NeverAsked()
        => throw new Xunit.Sdk.XunitException("Nothing was to be replaced, so nothing should have been asked.");

    private static (ConnectionCode Code, ECDiffieHellman Device) NewCode(string hostId = "host-1", string deviceId = "device-1")
    {
        var device = P256.Generate();
        var code = new ConnectionCode(Gateway, hostId, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            deviceId, P256.PublicRaw(device), RandomNumberGenerator.GetBytes(32));

        // Through the text, as the person carries it.
        return (ConnectionCode.Parse(code.Format()), device);
    }

    private static RemoteAccessService Service(
        EngineFixture fx, IHostKeys keys, Func<CancellationToken, Task<IGatewayConnection>> connect)
        => new(new RemoteAccessSettings
            {
                Enabled = true, GatewayUrl = Gateway.ToString(), HostId = keys.HostId, Token = "token"
            },
            keys, _ => throw new InvalidOperationException("No composition expected"), () => [], fx.Decisions,
            fx.PathOf("remote.db"), connect);

    /// <summary>The service's loop has ended, on its own: it stopped rather than went round again.</summary>
    private static async Task StoppedAsync(RemoteAccessService service)
    {
        var loop = (Task?)typeof(RemoteAccessService)
            .GetField("_loop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);
        Assert.NotNull(loop);
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the service.");
            await Task.Delay(20);
        }
    }

    private static byte[] SigningPublic(string database, string hostId)
    {
        using var store = new HostStore(database);
        using var keys = new HostKeyStore(store, hostId);
        return keys.SigningPublic;
    }

    /// <summary>An epoch key in the clear: this store never writes one so, so it reads as not this account's.</summary>
    private static void MakeUnreadable(string database)
        => Execute(database, "UPDATE host_keys SET secret = $value WHERE epoch = 1",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(string path, string sql, string value)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static long Count(string path, string table)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }
}
