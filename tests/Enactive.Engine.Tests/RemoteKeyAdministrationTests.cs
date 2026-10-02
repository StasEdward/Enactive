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
        var pairKey = code.PairKey;

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
            var (opened, signing) = Grants.Open(grant, device, pairKey, pinnedHostSigningPublic: null);
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
        Pairing.Apply(Fresh(code), keys);
        keys.Distrust(code.DeviceId);

        using var other = P256.Generate();
        var impostor = Fresh(code) with { DevicePublic = P256.PublicRaw(other) };
        Assert.Throws<InvalidOperationException>(() => Pairing.Apply(impostor, keys));
        Assert.Empty(keys.Live);

        Pairing.Apply(Fresh(code), keys);

        Assert.Equal(code.DeviceId, Assert.Single(keys.Live).DeviceId);
        Assert.Single(keys.PendingGrants());
    }

    /// <summary>A live device is never rebound to another key by a code, either.</summary>
    [WindowsFact]
    public void A_code_naming_a_live_device_with_another_key_is_refused_by_apply()
    {
        using var fx = new EngineFixture();
        var (code, device) = NewCode();
        using var _ = device;
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, code.HostId);
        Pairing.Apply(Fresh(code), keys);

        using var other = P256.Generate();
        Assert.Throws<InvalidOperationException>(
            () => Pairing.Apply(Fresh(code) with { DevicePublic = P256.PublicRaw(other) }, keys));
        Assert.Equal(code.DevicePublic, Assert.Single(keys.Live).PublicKey);
    }

    /// <summary>
    /// The secret is wiped from the code once it has authenticated the grants. Nothing needs it after,
    /// and a code object can outlive the click that applied it.
    /// </summary>
    [WindowsFact]
    public void Applying_a_code_wipes_its_pairing_secret_from_memory()
    {
        using var fx = new EngineFixture();
        var (code, device) = NewCode();
        using var _ = device;
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, code.HostId);

        Pairing.Apply(code, keys);

        Assert.All(code.PairingSecret, b => Assert.Equal(0, b));
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
        var secret = (byte[])code.PairingSecret.Clone();

        var outcome = await RemoteAccessService.ConnectWithCodeAsync(code, settings.RemoteAccess, database, NeverAsked);

        Assert.True(outcome.Paired);
        Assert.True(settings.Save(settingsFile), settings.LastSaveError);

        var stored = File.ReadAllBytes(settingsFile).Concat(File.ReadAllBytes(database));
        if (File.Exists(database + "-wal")) stored = stored.Concat(File.ReadAllBytes(database + "-wal"));
        var bytes = stored.ToArray();

        Assert.False(Contains(bytes, Encoding.ASCII.GetBytes(B64.Url(secret))));
        Assert.False(Contains(bytes, Encoding.ASCII.GetBytes(Convert.ToBase64String(secret))));
        Assert.False(Contains(bytes, secret));
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

        var declined = await RemoteAccessService.ConnectWithCodeAsync(second, settings, database, question =>
        {
            Assert.Equal(Pairing.ReplaceQuestion, question);
            asked++;
            return Task.FromResult(false);
        });

        Assert.Equal(1, asked);
        Assert.False(declined.Paired);
        Assert.Equal("host-a", settings.HostId);
        Assert.Equal(first.Token, settings.Token);
        Assert.Equal(signingBefore, SigningPublic(database, "host-a"));

        var replaced = await RemoteAccessService.ConnectWithCodeAsync(second, settings, database, question =>
        {
            Assert.Equal(Pairing.ReplaceQuestion, question);
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
    /// The one code applied without a question: an exact re-paste - the stored computer, the stored
    /// gateway, and a device this computer already trusts with that very key. It changes nothing a
    /// person would have to agree to, and re-queues that device's grants.
    /// </summary>
    [WindowsFact]
    public async Task An_exact_re_paste_is_applied_without_asking()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode("host-a", "device-a");
        using var _ = device;
        var text = code.Format();
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        var signingBefore = SigningPublic(database, "host-a");

        var outcome = await RemoteAccessService.ConnectWithCodeAsync(ConnectionCode.Parse(text), settings, database, NeverAsked);

        Assert.True(outcome.Paired);
        Assert.False(outcome.Replaced);
        Assert.Equal(signingBefore, SigningPublic(database, "host-a"));
        using var store = new HostStore(database);
        using var keys = new HostKeyStore(store, "host-a");
        Assert.Equal("device-a", Assert.Single(keys.Live).DeviceId);
    }

    /// <summary>
    /// The computer's id is no secret - it is on the settings pane, in the log, and known to the
    /// gateway's operator - so a code naming it proves nothing about who made it. One that moves the
    /// computer to another gateway is asked about, and a no leaves it where it was.
    /// </summary>
    [WindowsFact]
    public async Task A_code_for_this_computer_on_another_gateway_asks_before_moving_it()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode("host-a", "device-a");
        using var _ = device;
        var text = code.Format();
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        var elsewhere = ConnectionCode.Parse(text) with { Gateway = new Uri("https://attacker.example.test") };
        var questions = new List<string>();

        var declined = await RemoteAccessService.ConnectWithCodeAsync(elsewhere, settings, database, question =>
        {
            questions.Add(question);
            return Task.FromResult(false);
        });

        Assert.False(declined.Paired);
        Assert.Equal([Pairing.MoveQuestion(new Uri("https://attacker.example.test"))], questions);
        Assert.Contains("https://attacker.example.test", questions[0], StringComparison.Ordinal);
        Assert.Equal("https://remote.example.test", settings.GatewayUrl);

        var moved = await RemoteAccessService.ConnectWithCodeAsync(
            ConnectionCode.Parse(text) with { Gateway = new Uri("https://attacker.example.test") }, settings, database,
            _ => Task.FromResult(true));

        Assert.True(moved.Paired);
        Assert.Equal("https://attacker.example.test", settings.GatewayUrl);
    }

    /// <summary>
    /// A code for this computer that names a device it does not trust is asked about: applied silently,
    /// a crafted one would hand its own device every key this computer holds. A no admits nothing.
    /// </summary>
    [WindowsFact]
    public async Task A_code_for_this_computer_asks_before_admitting_a_new_device()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (first, firstDevice) = NewCode("host-a", "device-a");
        var (second, secondDevice) = NewCode("host-a", "device-b");
        using var _ = firstDevice;
        using var __ = secondDevice;
        var secondText = second.Format();
        await RemoteAccessService.ConnectWithCodeAsync(first, settings, database, NeverAsked);
        var questions = new List<string>();

        var declined = await RemoteAccessService.ConnectWithCodeAsync(second, settings, database, question =>
        {
            questions.Add(question);
            return Task.FromResult(false);
        });

        Assert.False(declined.Paired);
        Assert.Equal([Pairing.AdmitQuestion], questions);
        Assert.Equal(["device-a"], LiveDevices(database, "host-a"));
        Assert.Equal(first.Token, settings.Token);

        var admitted = await RemoteAccessService.ConnectWithCodeAsync(
            ConnectionCode.Parse(secondText), settings, database, _ => Task.FromResult(true));

        Assert.True(admitted.Paired);
        Assert.False(admitted.Replaced);
        Assert.Equal(["device-a", "device-b"], LiveDevices(database, "host-a"));
    }

    /// <summary>
    /// A revoked device is a device this computer does not trust: a code bringing it back is asked
    /// about like a new one - otherwise it would get back every epoch made since it was revoked - and on
    /// yes it is trusted again with the key it had (Retrust), never as a new trust of that id.
    /// </summary>
    [WindowsFact]
    public async Task A_code_for_a_revoked_device_asks_and_then_trusts_it_again_with_its_own_key()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode("host-a", "device-a");
        using var _ = device;
        var text = code.Format();
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        using (var store = new HostStore(database))
        using (var keys = new HostKeyStore(store, "host-a"))
        {
            keys.Distrust("device-a");
            keys.Rotate();
        }
        var questions = new List<string>();

        var outcome = await RemoteAccessService.ConnectWithCodeAsync(ConnectionCode.Parse(text), settings, database, question =>
        {
            questions.Add(question);
            return Task.FromResult(true);
        });

        Assert.True(outcome.Paired);
        Assert.Equal([Pairing.AdmitQuestion], questions);
        using (var store = new HostStore(database))
        using (var keys = new HostKeyStore(store, "host-a"))
        {
            var back = Assert.Single(keys.Live);
            Assert.Equal(code.DevicePublic, back.PublicKey);
            Assert.Equal(Pairing.AddedBy, back.AddedBy);
            Assert.Equal([1u, 2u], keys.PendingGrants().Select(p => p.Grant.Epoch).Order());
        }
    }

    /// <summary>
    /// A code naming a device this computer trusts with another key is refused with a sentence, and
    /// nothing is asked: there is no yes that could make it right, since a device id is never rebound.
    /// </summary>
    [WindowsFact]
    public async Task A_code_naming_a_live_device_with_another_key_is_refused_with_a_sentence()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode("host-a", "device-a");
        using var _ = device;
        var text = code.Format();
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        using var other = P256.Generate();

        var refused = await RemoteAccessService.ConnectWithCodeAsync(
            ConnectionCode.Parse(text) with { DevicePublic = P256.PublicRaw(other) }, settings, database, NeverAsked);

        Assert.False(refused.Paired);
        Assert.Equal(Pairing.AnotherKey, refused.Problem);
        using var store = new HostStore(database);
        using var keys = new HostKeyStore(store, "host-a");
        Assert.Equal(code.DevicePublic, Assert.Single(keys.Live).PublicKey);
    }

    /// <summary>
    /// Keys of THIS computer that this account cannot read are not replaced, even on a yes. The gateway
    /// and every browser pinned the old signing key, so a new one could never be delivered: every grant
    /// it signed would be refused as a bad grant, for good. The person is told to register the computer
    /// afresh, which is a code for another id - and that path does replace, after asking.
    /// </summary>
    [WindowsFact]
    public async Task Unreadable_keys_of_this_computer_are_reported_not_replaced()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode("host-a", "device-a");
        using var _ = device;
        var text = code.Format();
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        var unreadable = MakeUnreadable(database);

        var outcome = await RemoteAccessService.ConnectWithCodeAsync(ConnectionCode.Parse(text), settings, database, NeverAsked);

        Assert.False(outcome.Paired);
        Assert.Equal(Pairing.UnreadableForThisComputer, outcome.Problem);
        Assert.Equal(unreadable, Read(database, "SELECT secret FROM host_keys WHERE epoch = 1"));
        Assert.Equal(1L, Count(database, "host_signing"));

        var (fresh, freshDevice) = NewCode("host-new", "device-a");
        using var __ = freshDevice;
        var asked = 0;
        var replaced = await RemoteAccessService.ConnectWithCodeAsync(fresh, settings, database, question =>
        {
            Assert.Equal(Pairing.ReplaceQuestion, question);
            asked++;
            return Task.FromResult(true);
        });

        Assert.Equal(1, asked);
        Assert.True(replaced.Replaced);
        Assert.Equal("host-new", settings.HostId);
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
        var pairKey = code.PairKey;
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
        Grants.Open(grant, device, pairKey, pinnedHostSigningPublic: null);
    }

    /// <summary>
    /// A grant refused for good in a way that ends the connection - the credential revoked - stops the
    /// service, and the person reads why in the status line, not only in a notice list nobody reads.
    /// </summary>
    [WindowsFact]
    public async Task A_fatal_refusal_of_a_grant_stops_the_service_and_says_why()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode();
        using var _ = device;
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        var gateway = new FakeGateway
        {
            GrantRefusal = _ => new GatewayRefusedException(FaultCode.HostRevoked, "This computer was revoked.")
        };

        await using var service = new RemoteAccessService(settings, keys: null,
            _ => throw new InvalidOperationException("No composition expected"), () => [], fx.Decisions, database,
            connect: _ => Task.FromResult<IGatewayConnection>(gateway));
        service.Start();

        await StoppedAsync(service);
        Assert.Contains("remote access has stopped", service.Status, StringComparison.Ordinal);
        Assert.Contains("This computer was revoked.", service.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("Sync", gateway.Calls);
    }

    /// <summary>
    /// A connection that closes is replaced by a new one, and the new one says Hello before anything
    /// else. SignalR's own reconnect came back without it, so a gateway redeployed with another protocol
    /// meanwhile was sent Sync and Publish and the person never saw the sentence that names the problem.
    /// </summary>
    [Fact]
    public async Task A_connection_made_again_after_a_drop_says_hello_before_it_syncs()
    {
        using var fx = new EngineFixture();
        var first = new FakeGateway { OnSync = g => g.IsOpen = false };
        var second = new FakeGateway();
        var gateways = new Queue<FakeGateway>([first, second]);
        await using var service = new RemoteAccessService(
            new RemoteAccessSettings { Enabled = true, GatewayUrl = Gateway.ToString(), HostId = "host-1", Token = "token" },
            new FixedHostKeys(), _ => throw new InvalidOperationException("No composition expected"), () => [],
            fx.Decisions, fx.PathOf("remote.db"),
            connect: _ => Task.FromResult<IGatewayConnection>(gateways.Dequeue()),
            firstRetry: TimeSpan.FromMilliseconds(10));

        service.Start();
        await Until(() => second.Calls.Contains("Sync"));

        Assert.Equal(["Hello", "Sync"], first.Calls);
        Assert.Equal(["Hello", "Sync"], second.Calls.Take(2));
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

    private static Task<bool> NeverAsked(string question)
        => throw new Xunit.Sdk.XunitException($"Nothing should have been asked, and this was: {question}");

    /// <summary>
    /// A copy of a code with its own secret, for a test that applies one code more than once: applying
    /// wipes the secret of the code it was given.
    /// </summary>
    private static ConnectionCode Fresh(ConnectionCode code)
        => code with { PairingSecret = (byte[])code.PairingSecret.Clone() };

    private static string[] LiveDevices(string database, string hostId)
    {
        using var store = new HostStore(database);
        using var keys = new HostKeyStore(store, hostId);
        return [.. keys.Live.Select(d => d.DeviceId).Order()];
    }

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
    private static string MakeUnreadable(string database)
    {
        var clear = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Execute(database, "UPDATE host_keys SET secret = $value WHERE epoch = 1", clear);
        return clear;
    }

    private static object? Read(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

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
