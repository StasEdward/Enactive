namespace Enactive.Engine.Tests;

using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Enactive.App.Ui;
using Enactive.App.Ui.ViewModels;
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
///
/// <para>And adding another device from this computer with an invitation link (spec §5.3), where the
/// same secret authenticates the device's answer: the gateway relays that answer, and without the check
/// it could put its own key in it and receive every key this computer holds.</para>
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
    /// A credential the gateway refuses at the door - revoked, its account disabled or deleted - reaches the
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

        // Every cause the gateway's 401 can stand for, a deleted account among them: said as "revoked or
        // disabled", a person whose account was deleted was told something untrue about it.
        Assert.Equal(
            "The service refused this computer's credential - it was revoked, the account is disabled, or the "
            + "account was deleted. Make a new connection code in the browser and connect this computer with it.",
            GatewayCredentialRefusedException.Sentence);
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

    /// <summary>
    /// The service asks the gateway for answers to invitations only while this computer has one open -
    /// one more call every turn otherwise, for nothing - and says who was added when one is answered.
    /// The first connection has no invitation and closes after its turn; the second opens one at its
    /// Sync, and the same turn finds the device's answer.
    /// </summary>
    [WindowsFact]
    public async Task The_service_reads_enrollments_only_while_an_invitation_is_open_and_says_who_was_added()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode();
        using var _ = device;
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        using var phone = P256.Generate();
        RemoteAccessService? service = null;
        var invited = false;
        var first = new FakeGateway { OnSync = g => g.IsOpen = false };
        var second = new FakeGateway
        {
            OnSync = g =>
            {
                if (invited) return;
                invited = true;
                var link = service!.KeyAdministration!.InviteAsync(Gateway, CancellationToken.None).GetAwaiter().GetResult();
                g.Enroll(Enrolled(link, "phone", phone, "Phone"));
            }
        };
        var gateways = new Queue<FakeGateway>([first, second]);
        var admitted = new ConcurrentQueue<AdmittedDevice>();

        await using (service = new RemoteAccessService(settings, keys: null,
            _ => throw new InvalidOperationException("No composition expected"), () => [], fx.Decisions, database,
            connect: _ => Task.FromResult<IGatewayConnection>(gateways.Dequeue()),
            firstRetry: TimeSpan.FromMilliseconds(10)))
        {
            service.DeviceAdmitted += admitted.Enqueue;
            service.Start();
            await Until(() => !admitted.IsEmpty);
        }

        Assert.Equal(["Hello", "PublishGrants", "Sync"], first.Calls);
        Assert.Equal(new AdmittedDevice(Assert.Single(second.InvitesCreated), "phone", "Phone"), Assert.Single(admitted));
        Assert.Equal(["Hello", "Sync", "CreateInvite", "Enrollments", "AnsweredInvite"], second.Calls.Take(5));
        using var store = new HostStore(database);
        using var keys = new HostKeyStore(store, code.HostId);
        Assert.Equal(["device-1", "phone"], keys.Live.Select(d => d.DeviceId).Order());
    }

    /// <summary>
    /// A removal a browser sends reaches the trusted list through the running service: the device is
    /// distrusted, a new epoch is made, the person is told in the status line - and the gateway is not
    /// asked to remove the device again, since the browser did that before it sent the command.
    /// </summary>
    [WindowsFact]
    public async Task The_service_carries_out_a_removal_sent_from_a_browser()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode();
        using var _ = device;
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        using var phone = P256.Generate();
        HostCommand removal;
        using (var store = new HostStore(database))
        using (var keys = new HostKeyStore(store, code.HostId))
        {
            TrustDevice(keys, "phone", phone, "Phone");
            removal = new FixedHostKeys(code.HostId, keys.Current).Revoke("phone");
        }
        var gateway = new FakeGateway { Pending = [removal] };

        await using (var service = new RemoteAccessService(settings, keys: null,
            _ => throw new InvalidOperationException("No composition expected"), () => [], fx.Decisions, database,
            connect: _ => Task.FromResult<IGatewayConnection>(gateway)))
        {
            service.Start();
            await Until(() => service.Status.Contains(KeyAdministration.RemovedFromBrowser, StringComparison.Ordinal));
            Assert.StartsWith("Phone was removed from the browser", service.Status, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("RevokeDevice", gateway.Calls);
        using (var store = new HostStore(database))
        using (var keys = new HostKeyStore(store, code.HostId))
        {
            Assert.Equal([code.DeviceId], keys.Live.Select(d => d.DeviceId));
            Assert.Equal(2u, keys.Current.Epoch);
        }
    }

    /// <summary>
    /// A removal this computer received - written down and acknowledged - and had not yet carried out when the
    /// application closed is carried out when it starts again, whatever the gateway does meanwhile: Sync failing
    /// on every connection, or no connection at all. Handed back only after a Sync that worked, the removal
    /// waited for the gateway, and the device stayed trusted here exactly while the gateway was not there. It
    /// rotates once, however many times the service goes round.
    /// </summary>
    [WindowsTheory]
    [InlineData("sync fails")]
    [InlineData("unreachable")]
    public async Task A_removal_already_received_is_carried_out_without_the_gateway(string gatewayIs)
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode();
        using var _ = device;
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        using var phone = P256.Generate();
        using (var store = new HostStore(database))
        using (var keys = new HostKeyStore(store, code.HostId))
        {
            TrustDevice(keys, "phone", phone, "Phone");
            var removal = new FixedHostKeys(code.HostId, keys.Current).Revoke("phone");
            store.Accept(removal);
            store.MarkAcknowledged(removal.Id);
        }

        var gateway = new FakeGateway { SyncRefusal = new IOException("The connection to the gateway closed.") };
        var attempts = 0;
        Func<CancellationToken, Task<IGatewayConnection>> connect = gatewayIs == "unreachable"
            ? _ => { Interlocked.Increment(ref attempts); return Task.FromException<IGatewayConnection>(new IOException("The gateway is down.")); }
            : _ => { Interlocked.Increment(ref attempts); return Task.FromResult<IGatewayConnection>(gateway); };

        var said = new ConcurrentQueue<string>();
        await using (var service = new RemoteAccessService(settings, keys: null,
            _ => throw new InvalidOperationException("No composition expected"), () => [], fx.Decisions, database,
            connect, firstRetry: TimeSpan.FromMilliseconds(20)))
        {
            service.Changed += () => said.Enqueue(service.Status);
            service.Start();
            await Until(() => Volatile.Read(ref attempts) >= 3);
        }

        Assert.Single(said, status => status.StartsWith("Phone was removed from the browser", StringComparison.Ordinal));
        using (var store = new HostStore(database))
        using (var keys = new HostKeyStore(store, code.HostId))
        {
            Assert.Equal([code.DeviceId], keys.Live.Select(d => d.DeviceId));
            Assert.Equal(2u, keys.Current.Epoch);
        }
    }

    /// <summary>
    /// The runs a previous process left open are reported Interrupted once, at the first point this process
    /// reaches its records - whichever path gets there. Done only in the pass made without the gateway, and
    /// only once that pass had read the records: when they could not be read at start but the connection then
    /// opened them, the report was skipped, and a LATER failed connection made it - while this process's own
    /// runs were going, telling the phone they had been interrupted.
    /// </summary>
    [Fact]
    public async Task Runs_left_open_by_the_last_process_are_reported_on_the_first_connection_when_the_records_could_not_be_read_at_start()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var keys = new FixedHostKeys();
        using (var store = new HostStore(database))
        {
            store.Accept(keys.Start("command-1", "run-1"));
            store.BeginRun("command-1", "run-1");
        }

        // The records cannot be opened at start: a folder stands where the file is.
        var aside = database + ".aside";
        File.Move(database, aside);
        Directory.CreateDirectory(database);

        var first = new FakeGateway();
        var connections = 0;
        await using (var service = new RemoteAccessService(
            new RemoteAccessSettings { Enabled = true, GatewayUrl = Gateway.ToString(), HostId = keys.HostId, Token = "token" },
            keys, _ => throw new InvalidOperationException("No composition expected"), () => [], fx.Decisions, database,
            _ => Task.FromResult<IGatewayConnection>(Interlocked.Increment(ref connections) == 1 ? first : new FakeGateway()),
            firstRetry: TimeSpan.FromMilliseconds(20)))
        {
            var restored = 0;
            service.Changed += () =>
            {
                // Said on the loop's own thread, before it goes on to connect: the records are back by then.
                if (service.Status.StartsWith("Remote commands already received could not be read", StringComparison.Ordinal)
                    && Interlocked.Exchange(ref restored, 1) == 0)
                {
                    Directory.Delete(database);
                    File.Move(aside, database);
                }
            };

            service.Start();
            await Until(() => first.Calls.Contains("Sync"));
        }

        Assert.Equal([RemoteEventKind.Interrupted], first.Published.Where(e => e.RunId == "run-1").Select(e => e.Kind));
    }

    /// <summary>
    /// A computer that was asleep while a phone was lost and removed hears of the removal in the same Sync as
    /// whatever was queued under the old key - by the thief, or by the phone before it was lost: a start, and an
    /// endorsement of a stand-in device. The removal is carried out first, whatever order the gateway listed
    /// them in, so the rest are opened under the new key and refused as sealed before the removal. Run each on
    /// its own task, they opened under the old key whenever they got there before the rotation committed: the
    /// task ran, and the stand-in was trusted and given every later key.
    /// </summary>
    [WindowsFact]
    public async Task A_removal_in_a_sync_is_carried_out_before_the_commands_that_came_with_it()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode();
        using var _ = device;
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        using var phone = P256.Generate();
        using var standIn = P256.Generate();
        HostCommand[] batch;
        using (var store = new HostStore(database))
        using (var keys = new HostKeyStore(store, code.HostId))
        {
            TrustDevice(keys, "phone", phone, "Phone");
            var thief = new FixedHostKeys(code.HostId, keys.Current);
            batch =
            [
                thief.Start(commandId: "command-start", runId: "run-thief"),
                thief.Endorse("stand-in", B64.Url(P256.PublicRaw(standIn)), "Stand-in", commandId: "command-endorse"),
                thief.Revoke("phone", commandId: "command-revoke")
            ];
        }
        var gateway = new FakeGateway { Pending = [.. batch] };

        await using (var service = new RemoteAccessService(settings, keys: null,
            _ => throw new InvalidOperationException("No composition expected"), () => [], fx.Decisions, database,
            connect: _ => Task.FromResult<IGatewayConnection>(gateway)))
        {
            service.Start();
            await Until(() => gateway.Calls.Count(call => call == "Publish") > 0);
        }

        using (var store = new HostStore(database))
        using (var keys = new HostKeyStore(store, code.HostId))
        {
            Assert.Equal([code.DeviceId], keys.Live.Select(d => d.DeviceId));
            Assert.DoesNotContain(keys.Trusted, d => d.DeviceId == "stand-in");
            Assert.Equal(2u, keys.Current.Epoch);

            var ending = Assert.Single(gateway.Published, e => e.RunId == "run-thief");
            Assert.Equal(RemoteEventKind.Failed, ending.Kind);
            Assert.Equal(
                $"This computer refused the request: {Sealer.SealedBeforeRemoval}.",
                keys.Current.OpenText(ending.SealedDetail!, Ad.Event(keys.HostId, "run-thief", ending.Sequence, ending.Kind)));
        }
    }

    /// <summary>
    /// Removing a device does not wait for a connection: the part that keeps it from reading anything new
    /// is this computer's alone. The person is told the gateway will hear of it, not that it failed.
    /// </summary>
    [WindowsFact]
    public async Task A_device_is_removed_while_this_computer_is_offline_and_the_gateway_is_owed()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode();
        using var _ = device;
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        using var laptop = P256.Generate();
        using (var store = new HostStore(database))
        using (var keys = new HostKeyStore(store, code.HostId))
        {
            TrustDevice(keys, "laptop", laptop, "Laptop");
        }

        string said;
        await using (var service = new RemoteAccessService(settings, keys: null,
            _ => throw new InvalidOperationException("No composition expected"), () => [], fx.Decisions, database,
            connect: _ => Task.FromException<IGatewayConnection>(new IOException("The gateway is down.")),
            firstRetry: TimeSpan.FromHours(1)))
        {
            service.Start();

            said = await service.RevokeDeviceAsync("laptop", CancellationToken.None);

            Assert.Equal("Laptop was removed here; the gateway will be told when this computer next connects.", said);
            Assert.Equal(said, service.Status);
        }

        using (var store = new HostStore(database))
        using (var keys = new HostKeyStore(store, code.HostId))
        {
            Assert.Equal(2u, keys.Current.Epoch);
            Assert.Equal([code.DeviceId], keys.Live.Select(d => d.DeviceId));
            Assert.Equal(["laptop"], keys.OwedRevocations());
        }
    }

    /// <summary>
    /// A connection that cannot pass the removal on keeps it owed, and the next connection tells the
    /// gateway after its sync, before anything else is asked of it.
    /// </summary>
    [WindowsFact]
    public async Task The_next_connection_tells_the_gateway_of_a_removal_the_last_one_could_not()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var settings = new RemoteAccessSettings();
        var (code, device) = NewCode();
        using var _ = device;
        await RemoteAccessService.ConnectWithCodeAsync(code, settings, database, NeverAsked);
        using var laptop = P256.Generate();
        using (var store = new HostStore(database))
        using (var keys = new HostKeyStore(store, code.HostId))
        {
            TrustDevice(keys, "laptop", laptop, "Laptop");
        }
        var first = new FakeGateway { RevokeRefusal = new IOException("The connection to the gateway closed.") };
        var second = new FakeGateway();
        var gateways = new Queue<FakeGateway>([first, second]);

        await using (var service = new RemoteAccessService(settings, keys: null,
            _ => throw new InvalidOperationException("No composition expected"), () => [], fx.Decisions, database,
            connect: _ => Task.FromResult<IGatewayConnection>(gateways.Dequeue()),
            firstRetry: TimeSpan.FromMilliseconds(10)))
        {
            service.Start();
            await Until(() => first.Calls.Contains("Sync"));

            var said = await service.RevokeDeviceAsync("laptop", CancellationToken.None);
            Assert.Equal("Laptop was removed here; the gateway will be told when this computer next connects.", said);

            first.IsOpen = false;
            await Until(() => second.Revoked.Count > 0);
        }

        Assert.Equal(["laptop"], second.Revoked);
        var calls = second.Calls.ToList();
        Assert.Equal("Hello", calls[0]);
        Assert.True(calls.IndexOf("Sync") < calls.IndexOf("RevokeDevice"), string.Join(", ", calls));
        using var reopened = new HostStore(database);
        using var keys2 = new HostKeyStore(reopened, code.HostId);
        Assert.Empty(keys2.OwedRevocations());
    }

    // ── adding a device by invitation ───────────────────────────────────────

    /// <summary>
    /// The whole of an invitation: a link carrying the secret, the device's answer checked with the pair
    /// key, the device trusted under the label it gave, and a grant of every epoch waiting for it -
    /// authenticated with that same pair key, the only thing a device with nothing pinned can check.
    /// The invitation is single use: said answered to the gateway, and forgotten here.
    /// </summary>
    [WindowsFact]
    public async Task An_enrollment_with_a_valid_mac_gets_every_epoch()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        keys.Rotate();
        var gateway = new FakeGateway();
        var administration = new KeyAdministration(keys, gateway, TimeProvider.System);
        using var phone = P256.Generate();

        var link = await administration.InviteAsync(Gateway, CancellationToken.None);
        var pairKey = link.PairKey;
        gateway.Enroll(Enrolled(link, "phone", phone, "  Pixel 9  "));

        var admitted = await administration.AnswerEnrollmentsAsync(CancellationToken.None);

        Assert.Equal(new AdmittedDevice(link.InviteId, "phone", "Pixel 9"), Assert.Single(admitted));
        Assert.StartsWith($"https://remote.example.test/pair#v=2&i={link.InviteId}&p=", link.Format(), StringComparison.Ordinal);
        var trusted = Assert.Single(keys.Live);
        Assert.Equal("phone", trusted.DeviceId);
        Assert.Equal(P256.PublicRaw(phone), trusted.PublicKey);
        Assert.Equal("Pixel 9", trusted.Label);
        Assert.Equal(KeyAdministration.AddedBy, trusted.AddedBy);

        var grants = keys.PendingGrants().Select(p => p.Grant).ToList();
        Assert.Equal([1u, 2u], grants.Select(g => g.Epoch).Order());
        foreach (var grant in grants)
        {
            Assert.Equal("phone", grant.DeviceId);
            Assert.Equal(Grants.AuthByPairing(link.InviteId), grant.AuthBy);
            var (opened, signing) = Grants.Open(grant, phone, pairKey, pinnedHostSigningPublic: null);
            Assert.Equal(keys.Epoch(grant.Epoch)!.Secret.ToArray(), opened.Secret.ToArray());
            Assert.Equal(keys.SigningPublic, signing);
        }

        Assert.Equal([link.InviteId], gateway.InvitesCreated);
        Assert.Equal([link.InviteId], gateway.Answered);
        Assert.Null(keys.Invite(link.InviteId));
        Assert.False(keys.HasPendingInvites);
    }

    /// <summary>
    /// Review focus 2: the gateway relays the answer, and puts its own public key in it. The MAC was made
    /// over the device's key with a secret the gateway never saw, so it does not verify - and nothing is
    /// trusted or granted. The invitation is closed: whoever holds the link has shown they are not alone.
    /// </summary>
    [WindowsFact]
    public async Task An_enrollment_with_a_swapped_public_key_gets_nothing()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var gateway = new FakeGateway();
        var administration = new KeyAdministration(keys, gateway, TimeProvider.System);
        var notices = new List<AdmissionNotice>();
        administration.Noticed += notices.Add;
        using var phone = P256.Generate();
        using var gatewaysOwn = P256.Generate();

        var link = await administration.InviteAsync(Gateway, CancellationToken.None);
        gateway.Enroll(Enrolled(link, "phone", phone, "Phone") with { DevicePublic = B64.Url(P256.PublicRaw(gatewaysOwn)) });

        var admitted = await administration.AnswerEnrollmentsAsync(CancellationToken.None);

        Assert.Empty(admitted);
        Assert.Empty(keys.Trusted);
        Assert.Empty(keys.PendingGrants());
        Assert.Equal([link.InviteId], gateway.Answered);
        Assert.Null(keys.Invite(link.InviteId));
        var notice = Assert.Single(notices);
        Assert.Equal(link.InviteId, notice.InviteId);
        Assert.True(notice.Refused);
        Assert.Equal(KeyAdministration.Tampered, notice.Detail);
    }

    /// <summary>
    /// A link copied to two devices admits the first answer and no other: the invitation is forgotten
    /// once it is used, and a second answer to it - in the same batch, or later - is ignored.
    /// </summary>
    [WindowsFact]
    public async Task An_invite_answers_one_device_only()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var gateway = new FakeGateway();
        var administration = new KeyAdministration(keys, gateway, TimeProvider.System);
        using var phone = P256.Generate();
        using var laptop = P256.Generate();

        var link = await administration.InviteAsync(Gateway, CancellationToken.None);
        gateway.Enroll(Enrolled(link, "phone", phone, "Phone"));
        gateway.Enroll(Enrolled(link, "laptop", laptop, "Laptop"));

        Assert.Equal(["Phone"], Labels(await administration.AnswerEnrollmentsAsync(CancellationToken.None)));
        Assert.Empty(await administration.AnswerEnrollmentsAsync(CancellationToken.None));

        Assert.Equal("phone", Assert.Single(keys.Trusted).DeviceId);
        Assert.All(keys.PendingGrants(), p => Assert.Equal("phone", p.Grant.DeviceId));
    }

    /// <summary>
    /// An invitation the gateway would not register - too many open, say - is forgotten here too. Kept,
    /// it would be a secret stored for ten minutes that no device can ever answer, and the service would
    /// ask the gateway for answers every turn until it expired.
    /// </summary>
    [WindowsFact]
    public async Task An_invitation_the_gateway_refuses_is_forgotten()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var gateway = new FakeGateway
        {
            InviteRefusal = new GatewayRefusedException(FaultCode.InviteLimit, "Too many open invitations.")
        };
        var administration = new KeyAdministration(keys, gateway, TimeProvider.System);

        await Assert.ThrowsAsync<GatewayRefusedException>(() => administration.InviteAsync(Gateway, CancellationToken.None));

        Assert.False(keys.HasPendingInvites);
    }

    /// <summary>
    /// An answer that arrives after the invitation's ten minutes admits nothing, however good its MAC: a
    /// link found later in a chat or a screenshot must be useless. It is said answered, so the gateway
    /// stops handing it over.
    /// </summary>
    [WindowsFact]
    public async Task An_enrollment_for_an_expired_invitation_is_answered_and_ignored()
    {
        using var fx = new EngineFixture();
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1", clock);
        var gateway = new FakeGateway();
        var administration = new KeyAdministration(keys, gateway, clock);
        var notices = new List<AdmissionNotice>();
        administration.Noticed += notices.Add;
        using var phone = P256.Generate();

        var link = await administration.InviteAsync(Gateway, CancellationToken.None);
        gateway.Enroll(Enrolled(link, "phone", phone, "Phone"));
        clock.Advance(HostKeyStore.InviteLifetime);

        Assert.Empty(await administration.AnswerEnrollmentsAsync(CancellationToken.None));

        Assert.Empty(keys.Trusted);
        Assert.Empty(keys.PendingGrants());
        Assert.Equal([link.InviteId], gateway.Answered);
        Assert.False(Assert.Single(notices).Refused);
    }

    /// <summary>
    /// An answer naming a device this computer trusts with another key gets nothing, even with a good
    /// MAC: a device id is never rebound to a new key, and the device that has it keeps it.
    /// </summary>
    [WindowsFact]
    public async Task An_enrollment_for_a_device_trusted_with_another_key_gets_nothing()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var gateway = new FakeGateway();
        var administration = new KeyAdministration(keys, gateway, TimeProvider.System);
        var notices = new List<AdmissionNotice>();
        administration.Noticed += notices.Add;
        using var original = P256.Generate();
        using var other = P256.Generate();
        keys.Trust(new TrustedDevice("phone", P256.PublicRaw(original), "Phone", "test", DateTimeOffset.UtcNow, null));

        var link = await administration.InviteAsync(Gateway, CancellationToken.None);
        gateway.Enroll(Enrolled(link, "phone", other, "Phone"));

        Assert.Empty(await administration.AnswerEnrollmentsAsync(CancellationToken.None));

        Assert.Equal(P256.PublicRaw(original), Assert.Single(keys.Live).PublicKey);
        Assert.Empty(keys.PendingGrants());
        Assert.Equal([link.InviteId], gateway.Answered);
        Assert.Null(keys.Invite(link.InviteId));
        Assert.True(Assert.Single(notices).Refused);
    }

    /// <summary>
    /// A device this computer revoked can be brought back by an invitation - the person made the link
    /// on this computer - with the key it had, and it is owed every epoch, the ones made since too.
    /// </summary>
    [WindowsFact]
    public async Task A_revoked_device_is_trusted_again_by_an_invitation_with_its_own_key()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var gateway = new FakeGateway();
        var administration = new KeyAdministration(keys, gateway, TimeProvider.System);
        using var phone = P256.Generate();
        keys.Trust(new TrustedDevice("phone", P256.PublicRaw(phone), "Phone", "test", DateTimeOffset.UtcNow, null));
        keys.Distrust("phone");
        keys.Rotate();

        var link = await administration.InviteAsync(Gateway, CancellationToken.None);
        gateway.Enroll(Enrolled(link, "phone", phone, "Phone"));

        Assert.Equal(["Phone"], Labels(await administration.AnswerEnrollmentsAsync(CancellationToken.None)));

        var back = Assert.Single(keys.Live);
        Assert.Equal(KeyAdministration.AddedBy, back.AddedBy);
        Assert.Equal([1u, 2u], keys.PendingGrants().Select(p => p.Grant.Epoch).Order());
    }

    /// <summary>
    /// The pairing secret read back to check an answer, and the pair key made from it, are wiped once the
    /// answer is handled. Either one lets whoever finds it in memory make grants the new device accepts
    /// as this computer's; nothing needs them after. The link's own copy is the caller's, to wipe once it
    /// is no longer shown.
    /// </summary>
    [WindowsFact]
    public async Task Answering_wipes_the_pairing_secret_and_the_pair_key()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var gateway = new FakeGateway();
        var made = new List<byte[]>();
        var administration = new KeyAdministration(keys, gateway, TimeProvider.System, made.Add);
        using var phone = P256.Generate();

        var link = await administration.InviteAsync(Gateway, CancellationToken.None);
        gateway.Enroll(Enrolled(link, "phone", phone, "Phone"));

        Assert.Equal(["Phone"], Labels(await administration.AnswerEnrollmentsAsync(CancellationToken.None)));

        Assert.Equal(2, made.Count);
        Assert.All(made, secret =>
        {
            Assert.Equal(32, secret.Length);
            Assert.All(secret, b => Assert.Equal(0, b));
        });
        Assert.Contains(link.PairingSecret, b => b != 0);
    }

    /// <summary>
    /// An answer from a device this computer already trusts with that very key is granted again, and the
    /// first decision to trust it - its author, its time, its label - stands. This is the computer coming
    /// back after a crash between trusting the device and closing the invitation: the gateway hands the
    /// answer over again, and refusing it then would leave the device trusted with no key to read with.
    /// </summary>
    [WindowsFact]
    public async Task An_answer_from_a_device_already_trusted_with_that_key_is_granted_again()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        keys.Rotate();
        var gateway = new FakeGateway();
        var administration = new KeyAdministration(keys, gateway, TimeProvider.System);
        using var phone = P256.Generate();
        keys.Trust(new TrustedDevice("phone", P256.PublicRaw(phone), "First name", "test", DateTimeOffset.UtcNow, null));

        var link = await administration.InviteAsync(Gateway, CancellationToken.None);
        gateway.Enroll(Enrolled(link, "phone", phone, "Phone"));

        Assert.Equal(["Phone"], Labels(await administration.AnswerEnrollmentsAsync(CancellationToken.None)));

        var trusted = Assert.Single(keys.Live);
        Assert.Equal("test", trusted.AddedBy);
        Assert.Equal("First name", trusted.Label);
        Assert.Equal([1u, 2u], keys.PendingGrants().Select(p => p.Grant.Epoch).Order());
        Assert.Null(keys.Invite(link.InviteId));
    }

    /// <summary>
    /// A device key that is not a P-256 point - cut short, off the curve, not base64url at all - is refused
    /// like a MAC that fails, even when the MAC was made over exactly those bytes: the gateway relayed it,
    /// and a key nothing can be granted to is no device's.
    /// </summary>
    [WindowsTheory]
    [InlineData("short")]
    [InlineData("off-curve")]
    [InlineData("not-base64")]
    public async Task A_device_key_that_is_not_a_point_gets_nothing(string kind)
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var gateway = new FakeGateway();
        var administration = new KeyAdministration(keys, gateway, TimeProvider.System);
        var notices = new List<AdmissionNotice>();
        administration.Noticed += notices.Add;
        using var phone = P256.Generate();

        var link = await administration.InviteAsync(Gateway, CancellationToken.None);
        var raw = kind switch
        {
            "short" => P256.PublicRaw(phone)[..64],
            "off-curve" => [0x04, .. Enumerable.Repeat((byte)0x01, 64)],
            _ => P256.PublicRaw(phone)
        };
        var mac = Enrollment.Mac(link.PairKey, link.InviteId, "phone", raw);
        var encoded = kind == "not-base64" ? "not base64!" : B64.Url(raw);
        gateway.Enroll(new EnrollmentView(link.InviteId, "phone", encoded, "Phone", mac));

        Assert.Empty(await administration.AnswerEnrollmentsAsync(CancellationToken.None));

        Assert.Empty(keys.Trusted);
        Assert.Empty(keys.PendingGrants());
        Assert.Equal([link.InviteId], gateway.Answered);
        Assert.Equal(KeyAdministration.Tampered, Assert.Single(notices).Detail);
    }

    /// <summary>
    /// Anything that goes wrong admitting one answer refuses that answer and no other: its invitation is
    /// closed, said answered, and the rest of the batch is handled. Thrown out of the batch instead, it took
    /// the connection down, left the invitation open, and was met again every turn for ten minutes - and a
    /// device revoked between the trusted list being read and its grants being queued did exactly that.
    /// </summary>
    [WindowsFact]
    public async Task A_failure_admitting_one_answer_refuses_it_and_the_batch_goes_on()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var gateway = new FakeGateway();
        var reads = 0;
        // The first secret read fails, as the store would under the call: the first answer cannot be admitted.
        var administration = new KeyAdministration(keys, gateway, TimeProvider.System, _ =>
        {
            if (reads++ == 0) throw new InvalidOperationException("The store failed under the call.");
        });
        var notices = new List<AdmissionNotice>();
        administration.Noticed += notices.Add;
        using var phone = P256.Generate();
        using var laptop = P256.Generate();

        var first = await administration.InviteAsync(Gateway, CancellationToken.None);
        var second = await administration.InviteAsync(Gateway, CancellationToken.None);
        gateway.Enroll(Enrolled(first, "phone", phone, "Phone"));
        gateway.Enroll(Enrolled(second, "laptop", laptop, "Laptop"));

        Assert.Equal(["Laptop"], Labels(await administration.AnswerEnrollmentsAsync(CancellationToken.None)));

        Assert.Equal("laptop", Assert.Single(keys.Trusted).DeviceId);
        Assert.Equal([first.InviteId, second.InviteId], gateway.Answered);
        Assert.Null(keys.Invite(first.InviteId));
        var notice = Assert.Single(notices);
        Assert.Equal(first.InviteId, notice.InviteId);
        Assert.True(notice.Refused);
        Assert.DoesNotContain("store failed", notice.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// An invitation id the gateway sent that is not one this computer could have made is not repeated in
    /// what the log says - it is text of the gateway's choosing, line breaks and all - and is not sent back.
    /// </summary>
    [WindowsFact]
    public async Task An_answer_naming_no_invitation_id_is_set_aside_without_repeating_it()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var gateway = new FakeGateway();
        var administration = new KeyAdministration(keys, gateway, TimeProvider.System);
        var notices = new List<AdmissionNotice>();
        administration.Noticed += notices.Add;
        gateway.Enroll(new EnrollmentView("Remote access: all clear\r\nforged", "phone", "x", "Phone", "x"));

        Assert.Empty(await administration.AnswerEnrollmentsAsync(CancellationToken.None));

        var notice = Assert.Single(notices);
        Assert.False(notice.Refused);
        Assert.Contains("<not an id>", notice.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("forged", notice.Detail, StringComparison.Ordinal);
        Assert.Empty(gateway.Answered);
    }

    /// <summary>
    /// A device's label is text a stranger may have chosen, shown in the trusted list, the window and the
    /// log. What would break a line there, reorder it or hide in it is replaced; it is kept to 80
    /// characters without cutting a character in half.
    /// </summary>
    [Theory]
    [InlineData("Phone\r\nRemote access: forged", "Phone  Remote access: forged")]
    [InlineData("Phone\u2028second line\u2029third", "Phone second line third")]
    [InlineData("txt.\u202Eexe", "txt. exe")]
    [InlineData("zero\u200Bwidth", "zero width")]
    [InlineData("   ", KeyAdministration.UnnamedDevice)]
    [InlineData(null, KeyAdministration.UnnamedDevice)]
    public void A_label_is_kept_to_one_plain_line(string? label, string expected)
        => Assert.Equal(expected, KeyAdministration.CleanLabel(label));

    [Fact]
    public void A_label_loses_half_characters_and_is_cut_at_80_without_splitting_one()
    {
        var face = "\U0001F600";

        // Half a pair on its own is no character: it shows as a box, or hides what follows. Built at run
        // time - an attribute argument goes into metadata as UTF-8, which has no way to hold one.
        Assert.Equal("lone   high", KeyAdministration.CleanLabel("lone " + (char)0xD800 + " high"));
        Assert.Equal("lone   low", KeyAdministration.CleanLabel("lone " + (char)0xDC00 + " low"));

        Assert.Equal(new string('a', 80), KeyAdministration.CleanLabel(new string('a', 100)));
        // 79 letters and a two-unit character: the character would straddle the 80th place, so it goes.
        Assert.Equal(new string('a', 79), KeyAdministration.CleanLabel(new string('a', 79) + face + "tail"));
        Assert.Equal(new string('a', 78) + face, KeyAdministration.CleanLabel(new string('a', 78) + face + "tail"));
    }

    // ── removing a device, and a device a browser vouches for ───────────────

    /// <summary>
    /// Spec §5.4: the removed device is no longer trusted, a new epoch is made, and every device that
    /// remains is owed it. A removal the browser sent is not passed back to the gateway: the browser
    /// removed the device there before it sent the command.
    /// </summary>
    [WindowsFact]
    public async Task Revoking_a_device_rotates_and_grants_the_rest()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var gateway = new FakeGateway();
        var administration = new KeyAdministration(keys, gateway, TimeProvider.System);
        var rotations = new List<KeyRotated>();
        administration.Rotated += rotations.Add;
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        using var tablet = P256.Generate();
        TrustDevice(keys, "phone", phone, "Phone");
        TrustDevice(keys, "laptop", laptop, "Laptop");
        TrustDevice(keys, "tablet", tablet, "Tablet");

        await administration.RevokeAsync("laptop", KeyAdministration.RemovedFromBrowser, CancellationToken.None);

        Assert.Equal(2u, keys.Current.Epoch);
        Assert.Equal(["phone", "tablet"], keys.Live.Select(d => d.DeviceId).Order());
        Assert.NotNull(Assert.Single(keys.Trusted, d => d.DeviceId == "laptop").RevokedAt);

        var grants = keys.PendingGrants().Select(p => p.Grant).ToList();
        Assert.Equal(["phone", "tablet"], grants.Select(g => g.DeviceId).Order());
        Assert.All(grants, grant =>
        {
            Assert.Equal(2u, grant.Epoch);
            Assert.Equal(Grants.AuthByHost, grant.AuthBy);
        });

        Assert.Equal(new KeyRotated("laptop", "Laptop", 2u, KeyAdministration.RemovedFromBrowser), Assert.Single(rotations));
        Assert.Empty(gateway.Calls);
    }

    /// <summary>
    /// Review focus 4. The removed device keeps its private key and every grant it was ever given -
    /// delivered, or still queued when it was removed - and with the gateway's help it can read every
    /// grant made to anyone. None of it opens what this computer seals after the removal: no grant names
    /// it, the grants it can read are wrapped for other keys, and every epoch key it holds is an old one.
    /// The device that stays opens its new grant against the signing key it pinned, and reads the event.
    /// </summary>
    [WindowsFact]
    public async Task The_revoked_device_cannot_open_anything_sealed_after_its_revocation()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        keys.Rotate();
        var administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        TrustDevice(keys, "phone", phone, "Phone");
        TrustDevice(keys, "laptop", laptop, "Laptop");
        var pinned = keys.SigningPublic;

        // Everything the laptop was given: a grant of every epoch, delivered, and the same again queued.
        var given = keys.All.Select(k => Grants.CreateSigned(keys.HostId, "laptop", P256.PublicRaw(laptop), k, keys.Signer)).ToList();
        foreach (var grant in keys.All.Select(k => Grants.CreateSigned(keys.HostId, "laptop", P256.PublicRaw(laptop), k, keys.Signer)))
        {
            keys.EnqueueGrant(grant);
        }

        await administration.RevokeAsync("laptop", KeyAdministration.RemovedHere, CancellationToken.None);

        var sealer = new Sealer(keys, TimeProvider.System);
        var ad = Ad.Event(keys.HostId, "run-1", 1, RemoteEventKind.Progress);
        var detail = sealer.Detail("run-1", 1, RemoteEventKind.Progress, "Written after the laptop was removed");

        var pending = keys.PendingGrants().Select(p => p.Grant).ToList();
        Assert.DoesNotContain(pending, g => g.DeviceId == "laptop");

        var held = given.Select(g => Grants.Open(g, laptop, default, pinned).Key).ToList();
        Assert.Equal([1u, 2u], held.Select(k => k.Epoch));
        Assert.All(held, key => Assert.ThrowsAny<CryptographicException>(() => key.OpenText(detail, ad)));
        Assert.All(pending, grant => Assert.ThrowsAny<CryptographicException>(() => Grants.Open(grant, laptop, default, pinned)));

        var (phoneKey, _) = Grants.Open(Assert.Single(pending, g => g.DeviceId == "phone"), phone, default, pinned);
        Assert.Equal(3u, phoneKey.Epoch);
        Assert.Equal("Written after the laptop was removed", phoneKey.OpenText(detail, ad));
    }

    /// <summary>
    /// A rotation grant is signed with the computer's signing key, and nothing else will do: an epoch key
    /// cannot sign one, since the removed device holds those. It opens against the key the device pinned,
    /// and not with nothing pinned or with another computer's key pinned.
    /// </summary>
    [WindowsFact]
    public async Task Rotation_grants_are_signed_and_open_against_the_pinned_signing_key()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        using var stranger = P256.GenerateSigning();
        TrustDevice(keys, "phone", phone, "Phone");
        TrustDevice(keys, "laptop", laptop, "Laptop");
        var pinned = keys.SigningPublic;

        await administration.RevokeAsync("laptop", KeyAdministration.RemovedHere, CancellationToken.None);

        var grant = Assert.Single(keys.PendingGrants()).Grant;
        Assert.Equal(Grants.AuthByHost, grant.AuthBy);
        Assert.Equal(B64.Url(pinned), grant.HostSigningPublic);

        var (key, signing) = Grants.Open(grant, phone, default, pinned);
        Assert.Equal(keys.Current.Secret.ToArray(), key.Secret.ToArray());
        Assert.Equal(pinned, signing);

        Assert.Throws<EnvelopeException>(() => Grants.Open(grant, phone, default, pinnedHostSigningPublic: null));
        Assert.Throws<EnvelopeException>(() => Grants.Open(grant, phone, default, P256.SigningPublicRaw(stranger)));
    }

    /// <summary>
    /// Every key holder seals with the same key, so this computer cannot tell which device sealed a
    /// command. After a removal it acts only on what is sealed under the new epoch, which the removed
    /// device does not have: the removed device's own removal of another device does nothing. A start a
    /// remaining device sent just before it heard of the new key is refused with a request to send it
    /// again - not called forged, which it is not.
    /// </summary>
    [WindowsFact]
    public async Task A_command_sealed_under_the_old_epoch_after_rotation_is_refused()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        var runner = Runner(store, keys, () => administration);
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        TrustDevice(keys, "phone", phone, "Phone");
        TrustDevice(keys, "laptop", laptop, "Laptop");
        var before = new FixedHostKeys(keys.HostId, keys.Current);

        await administration.RevokeAsync("laptop", KeyAdministration.RemovedHere, CancellationToken.None);

        var sealer = new Sealer(keys, TimeProvider.System);
        var refused = Assert.Throws<CommandRefusedException>(() => sealer.OpenStart(before.Start()));
        Assert.Equal(Sealer.SealedBeforeRemoval, refused.Message);

        await runner.ApplyAsync(before.Revoke("phone"));
        Assert.Equal(["phone"], keys.Live.Select(d => d.DeviceId));
        Assert.Equal(2u, keys.Current.Epoch);
        Assert.Contains(runner.Notices, n => n.Kind == "Refused" && n.Detail.EndsWith(Sealer.SealedBeforeRemoval, StringComparison.Ordinal));

        var oldStart = before.Start(commandId: "command-old", runId: "run-old");
        store.Accept(oldStart);
        await runner.ApplyAsync(oldStart);
        var ending = Assert.Single(store.NextOwed(), o => o.RunId == "run-old").Event;
        Assert.Equal(
            $"This computer refused the request: {Sealer.SealedBeforeRemoval}.",
            keys.Current.OpenText(ending.SealedDetail!, Ad.Event(keys.HostId, "run-old", ending.Sequence, ending.Kind)));

        var after = new FixedHostKeys(keys.HostId, keys.Current);
        Assert.Equal("run-1", sealer.OpenStart(after.Start()).RunId);
    }

    /// <summary>
    /// The epoch is written in the envelope in the clear, so a gateway can put an old one on a forgery.
    /// What does not open under that old key is refused as forged, not as a command to send again - or
    /// every forgery could be made to read as an honest phone's late command.
    /// </summary>
    [WindowsFact]
    public async Task A_forgery_naming_an_old_epoch_is_refused_as_forged_not_as_stale()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        var runner = Runner(store, keys, () => administration);
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        TrustDevice(keys, "phone", phone, "Phone");
        TrustDevice(keys, "laptop", laptop, "Laptop");
        await administration.RevokeAsync("laptop", KeyAdministration.RemovedHere, CancellationToken.None);
        var gateway = new FixedHostKeys(keys.HostId, HostKey.Create(1));

        var refused = Assert.Throws<CommandRefusedException>(() => new Sealer(keys, TimeProvider.System).OpenStart(gateway.Start()));
        Assert.Equal(Sealer.NotSealedHere, refused.Message);
        Assert.False(refused.SendAgain);

        var forged = gateway.Start(commandId: "command-forged", runId: "run-forged");
        store.Accept(forged);
        await runner.ApplyAsync(forged);
        var ending = Assert.Single(store.NextOwed(), o => o.RunId == "run-forged").Event;
        Assert.EndsWith("It did not come from a device this computer trusts.",
            keys.Current.OpenText(ending.SealedDetail!, Ad.Event(keys.HostId, "run-forged", ending.Sequence, ending.Kind)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A trusted browser that admitted a device by its own invitation sends this computer an endorsement,
    /// and the device is trusted here under the label it was given, kept to one line - so the next
    /// removal grants it the new key. Nothing is granted to it now: the browser that admitted it granted
    /// it the keys it holds. The same endorsement again, from another browser, changes nothing.
    /// </summary>
    [WindowsFact]
    public async Task An_endorsed_device_is_trusted()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        var runner = Runner(store, keys, () => administration);
        using var phone = P256.Generate();
        using var tablet = P256.Generate();
        TrustDevice(keys, "phone", phone, "Phone");
        var browser = new FixedHostKeys(keys.HostId, keys.Current);

        await runner.ApplyAsync(browser.Endorse("tablet", B64.Url(P256.PublicRaw(tablet)), "  Tablet\r\nforged line"));
        await runner.ApplyAsync(browser.Endorse("tablet", B64.Url(P256.PublicRaw(tablet)), "Another name", commandId: "command-e2"));

        Assert.Empty(runner.Notices);
        var endorsed = Assert.Single(keys.Live, d => d.DeviceId == "tablet");
        Assert.Equal(P256.PublicRaw(tablet), endorsed.PublicKey);
        Assert.Equal("Tablet  forged line", endorsed.Label);
        Assert.Equal(KeyAdministration.EndorsedBy, endorsed.AddedBy);
        Assert.Empty(keys.PendingGrants());

        await runner.ApplyAsync(browser.Revoke("phone"));

        var grant = Assert.Single(keys.PendingGrants()).Grant;
        Assert.Equal(("tablet", 2u), (grant.DeviceId, grant.Epoch));
    }

    /// <summary>
    /// An endorsement is sealed under an epoch key, which a device removed since may still hold. So it
    /// never brings a removed device back - that is an admission, made on this computer - and never binds
    /// a known id to another key, which would hand that key the next epoch under someone else's name. A
    /// key that is not a P-256 point is no device's either.
    /// </summary>
    [WindowsTheory]
    [InlineData("removed", KeyAdministration.RemovedCannotBeEndorsed)]
    [InlineData("another-key", KeyAdministration.AnotherKey)]
    [InlineData("not-a-point", KeyAdministration.NotADeviceKey)]
    public async Task An_endorsement_that_would_rebind_or_bring_back_a_device_is_refused(string kind, string reason)
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        var runner = Runner(store, keys, () => administration);
        using var phone = P256.Generate();
        using var other = P256.Generate();
        TrustDevice(keys, "phone", phone, "Phone");
        if (kind == "removed") keys.Distrust("phone");
        var browser = new FixedHostKeys(keys.HostId, keys.Current);
        var key = kind switch
        {
            "another-key" => B64.Url(P256.PublicRaw(other)),
            "not-a-point" => B64.Url(P256.PublicRaw(phone)[..64]),
            _ => B64.Url(P256.PublicRaw(phone))
        };
        var deviceId = kind == "not-a-point" ? "tablet" : "phone";

        var command = browser.Endorse(deviceId, key, "Phone");
        await runner.ApplyAsync(command);

        var notice = Assert.Single(runner.Notices);
        Assert.Equal("Refused", notice.Kind);
        Assert.Contains(command.Id, notice.Detail, StringComparison.Ordinal);
        Assert.EndsWith(": " + reason, notice.Detail, StringComparison.Ordinal);

        var known = Assert.Single(keys.Trusted);
        Assert.Equal(P256.PublicRaw(phone), known.PublicKey);
        Assert.Equal(kind == "removed", known.RevokedAt is not null);
        Assert.Empty(keys.PendingGrants());
    }

    /// <summary>
    /// A computer that holds no key store - one fixed key, as the oldest tests have it - has no trusted
    /// list to change. A device command there is said refused, with a reason, and is not an exception
    /// thrown out of the run.
    /// </summary>
    [Fact]
    public async Task A_device_command_on_a_computer_that_cannot_manage_devices_is_refused()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        var keys = new FixedHostKeys();
        var runner = new RemoteRunner(store, new RemoteApprovals(), keys.Sealer(),
            (_, _, _) => throw new InvalidOperationException("No run expected"), () => null);

        await runner.ApplyAsync(keys.Revoke("phone"));
        await runner.ApplyAsync(keys.Endorse("tablet", "key", "Tablet"));

        Assert.Equal(2, runner.Notices.Count);
        Assert.All(runner.Notices, notice =>
        {
            Assert.Equal("Refused", notice.Kind);
            Assert.EndsWith(KeyAdministration.CannotManageDevices, notice.Detail, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Removing a device here also tells the gateway, so it stops serving that device - its API calls and
    /// the grants it holds. The computer's own part comes first: should the gateway be out of reach, the
    /// device already reads nothing new.
    /// </summary>
    [WindowsFact]
    public async Task Removing_a_device_on_this_computer_tells_the_gateway_too()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var gateway = new FakeGateway();
        var administration = new KeyAdministration(keys, gateway, TimeProvider.System);
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        TrustDevice(keys, "phone", phone, "Phone");
        TrustDevice(keys, "laptop", laptop, "Laptop");

        await administration.RevokeHereAsync("laptop", CancellationToken.None);

        Assert.Equal(["laptop"], gateway.Revoked);
        Assert.Equal(2u, keys.Current.Epoch);
        Assert.Equal(["phone"], keys.Live.Select(d => d.DeviceId));
    }

    /// <summary>
    /// A removal that fails part of the way changes nothing - the device is still trusted, the epoch the
    /// one it was - so it can simply be made again, and the second time rotates once. Made as three steps,
    /// the failure left the device shown as removed while it read everything new, and a second removal was
    /// a no-op.
    /// </summary>
    [WindowsFact]
    public async Task A_removal_that_fails_part_way_leaves_the_device_trusted_and_can_be_made_again()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("remote.db");
        using var store = new HostStore(path);
        using var keys = new HostKeyStore(store, "host-1");
        var administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        var rotations = new List<KeyRotated>();
        administration.Rotated += rotations.Add;
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        TrustDevice(keys, "phone", phone, "Phone");
        TrustDevice(keys, "laptop", laptop, "Laptop");
        HostKeyStoreTests.TrustUngrantable(path, "broken");

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => administration.RevokeAsync("laptop", KeyAdministration.RemovedFromBrowser, CancellationToken.None));

        Assert.Equal(1u, keys.Current.Epoch);
        Assert.Contains("laptop", keys.Live.Select(d => d.DeviceId));
        Assert.Empty(keys.PendingGrants());
        Assert.Empty(rotations);

        keys.Distrust("broken");
        await administration.RevokeAsync("laptop", KeyAdministration.RemovedFromBrowser, CancellationToken.None);

        Assert.Equal(2u, keys.Current.Epoch);
        Assert.Equal(["phone"], keys.Live.Select(d => d.DeviceId));
        Assert.Single(rotations);
    }

    /// <summary>
    /// The gateway half of a removal made here is not lost when the gateway cannot be reached: the device
    /// is removed on this computer all the same, the gateway is owed word of it, and the next connection
    /// tells it. Said as a failure, it contradicted the list - which showed the device removed - and was
    /// never retried.
    /// </summary>
    [WindowsFact]
    public async Task A_removal_the_gateway_could_not_be_told_of_is_owed_and_told_on_the_next_connection()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var unreachable = new FakeGateway { RevokeRefusal = new IOException("The connection to the gateway closed.") };
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        TrustDevice(keys, "phone", phone, "Phone");
        TrustDevice(keys, "laptop", laptop, "Laptop");

        var told = await new KeyAdministration(keys, unreachable, TimeProvider.System).RevokeHereAsync("laptop", CancellationToken.None);

        Assert.False(told);
        Assert.Contains("RevokeDevice", unreachable.Calls);
        Assert.Equal(2u, keys.Current.Epoch);
        Assert.Equal(["phone"], keys.Live.Select(d => d.DeviceId));
        Assert.Equal(["laptop"], keys.OwedRevocations());

        var next = new FakeGateway();
        Assert.True(await new KeyAdministration(keys, next, TimeProvider.System).SettleOwedRevocationsAsync(CancellationToken.None));

        Assert.Equal(["laptop"], next.Revoked);
        Assert.Empty(keys.OwedRevocations());
    }

    /// <summary>
    /// A gateway that answers it has no such device settles what is owed: there is nothing left there to
    /// stop serving, and asking again on every connection would be asking for ever.
    /// </summary>
    [WindowsFact]
    public async Task A_gateway_that_no_longer_has_the_device_settles_what_is_owed()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var gateway = new FakeGateway { RevokeRefusal = new GatewayRefusedException("not-found", "That device is not registered.") };
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        TrustDevice(keys, "phone", phone, "Phone");
        TrustDevice(keys, "laptop", laptop, "Laptop");

        Assert.True(await new KeyAdministration(keys, gateway, TimeProvider.System).RevokeHereAsync("laptop", CancellationToken.None));

        Assert.Empty(keys.OwedRevocations());
        Assert.Equal(2u, keys.Current.Epoch);
    }

    /// <summary>
    /// A removal of a device that is already removed - by a second browser, or on the desktop after the
    /// browser - makes no second epoch: there is nothing more to keep from it. Nor does one this computer never
    /// trusted, which holds no key of it; the removal is kept, so the device is never trusted later
    /// (A_removal_before_an_endorsement_keeps_the_device_out).
    /// </summary>
    [WindowsFact]
    public async Task Removing_a_removed_or_unknown_device_rotates_nothing()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        using var phone = P256.Generate();
        using var laptop = P256.Generate();
        TrustDevice(keys, "phone", phone, "Phone");
        TrustDevice(keys, "laptop", laptop, "Laptop");

        await administration.RevokeAsync("laptop", KeyAdministration.RemovedFromBrowser, CancellationToken.None);
        await administration.RevokeAsync("laptop", KeyAdministration.RemovedHere, CancellationToken.None);
        await administration.RevokeAsync("stranger", KeyAdministration.RemovedFromBrowser, CancellationToken.None);

        Assert.Equal(2u, keys.Current.Epoch);
        Assert.Single(keys.PendingGrants());
        Assert.Equal(["phone"], keys.Live.Select(d => d.DeviceId));
    }

    /// <summary>
    /// A browser invites a device and endorses it, then the device is removed; the computer, off meanwhile, is
    /// handed the removal before the endorsement - its own order, or a gateway holding the endorsement back. The
    /// removal named a device this computer had never trusted and was forgotten, so the endorsement then trusted
    /// the removed device, and every key made after was granted to it. Kept, the removal refuses the endorsement
    /// as one of a removed device.
    /// </summary>
    [WindowsFact]
    public async Task A_removal_before_an_endorsement_keeps_the_device_out()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("remote.db"));
        using var keys = new HostKeyStore(store, "host-1");
        var administration = new KeyAdministration(keys, new FakeGateway(), TimeProvider.System);
        var runner = Runner(store, keys, () => administration);
        using var phone = P256.Generate();
        using var standIn = P256.Generate();
        TrustDevice(keys, "phone", phone, "Phone");
        var browser = new FixedHostKeys(keys.HostId, keys.Current);

        await runner.ApplyAsync(browser.Revoke("stand-in"));
        var endorsement = browser.Endorse("stand-in", B64.Url(P256.PublicRaw(standIn)), "Stand-in");
        await runner.ApplyAsync(endorsement);

        var notice = Assert.Single(runner.Notices);
        Assert.Contains(endorsement.Id, notice.Detail, StringComparison.Ordinal);
        Assert.EndsWith(": " + KeyAdministration.RemovedCannotBeEndorsed, notice.Detail, StringComparison.Ordinal);
        Assert.Equal(["phone"], keys.Live.Select(d => d.DeviceId));
        Assert.Equal(1u, keys.Current.Epoch);
        Assert.Empty(keys.PendingGrants());
    }

    /// <summary>
    /// The removal kept for a device this computer never trusted is no device the person added, and the desktop's
    /// list of trusted devices does not show it: listed, it was a row with no name the person could not place.
    /// </summary>
    [WindowsFact]
    public async Task The_desktop_list_leaves_out_a_removal_of_a_device_never_trusted()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("keys.db"));
        using var keys = new HostKeyStore(store, "host-1");
        using var phone = P256.Generate();
        TrustDevice(keys, "phone", phone, "Phone");
        keys.Distrust("stand-in");

        await using var service = Service(fx, keys, _ => throw new InvalidOperationException("No connection expected"));

        Assert.Equal(["phone"], service.TrustedDevices().Select(d => d.DeviceId));
    }

    // ── the window and the pane, without a window ───────────────────────────

    /// <summary>
    /// The Add a device window settles on its OWN invitation's answer only. Settled by another one's -
    /// opened elsewhere, or left from before - it took its link down without withdrawing it, and the link
    /// stayed answerable for the rest of its ten minutes, perhaps still in the clipboard.
    /// </summary>
    [Fact]
    public async Task The_window_settles_on_its_own_invitation_only_and_withdraws_it_otherwise()
    {
        var devices = new FakeInvitations();
        var window = new AddDeviceViewModel(devices, TimeProvider.System);
        await window.StartAsync(CancellationToken.None);
        var takenDown = new List<string>();
        window.LinkTakenDown += takenDown.Add;

        devices.Admit(new AdmittedDevice("another-invitation", "laptop", "Laptop"));

        Assert.False(window.IsSettled);
        Assert.Equal(AddDeviceViewModel.Waiting, window.Status);
        Assert.True(window.IsLinkShown);

        window.Closed();

        Assert.Equal([devices.Made!.InviteId], devices.Withdrawn);
        Assert.All(devices.Made.PairingSecret, b => Assert.Equal(0, b));
        Assert.Single(takenDown);
    }

    [Fact]
    public async Task The_windows_own_answer_settles_it_and_nothing_is_withdrawn()
    {
        var devices = new FakeInvitations();
        var window = new AddDeviceViewModel(devices, TimeProvider.System);
        var finished = 0;
        window.Admitted += () => finished++;
        await window.StartAsync(CancellationToken.None);

        devices.Admit(new AdmittedDevice(devices.Made!.InviteId, "phone", "Phone"));
        window.Closed();

        Assert.True(window.IsSettled);
        Assert.False(window.IsLinkShown);
        Assert.StartsWith("Phone was added", window.Status, StringComparison.Ordinal);
        Assert.Equal(1, finished);
        Assert.Empty(devices.Withdrawn);
    }

    /// <summary>A refusal of another invitation's answer is not this window's to show.</summary>
    [Fact]
    public async Task The_window_shows_a_refusal_of_its_own_invitation_only()
    {
        var devices = new FakeInvitations();
        var window = new AddDeviceViewModel(devices, TimeProvider.System);
        await window.StartAsync(CancellationToken.None);

        devices.Notice(new AdmissionNotice("another-invitation", true, KeyAdministration.Tampered));
        Assert.False(window.IsSettled);

        devices.Notice(new AdmissionNotice(devices.Made!.InviteId, true, KeyAdministration.Tampered));
        Assert.True(window.IsSettled);
        Assert.False(window.IsLinkShown);
        Assert.Equal(KeyAdministration.Tampered, window.Problem);
    }

    /// <summary>
    /// When the ten minutes run out the window says nobody answered in time - closing on its own left a
    /// person who looked back at the screen wondering whether it had worked - and the invitation is withdrawn.
    /// </summary>
    [Fact]
    public async Task The_window_says_when_nobody_answered_in_time()
    {
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        var devices = new FakeInvitations();
        var window = new AddDeviceViewModel(devices, clock);
        await window.StartAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(9));
        window.Tick();
        Assert.Equal("The link stops working in 1:00.", window.Countdown);

        clock.Advance(TimeSpan.FromMinutes(1));
        window.Tick();

        Assert.True(window.IsSettled);
        Assert.False(window.IsLinkShown);
        Assert.Equal(AddDeviceViewModel.Expired, window.Status);
        Assert.Equal([devices.Made!.InviteId], devices.Withdrawn);
        window.Closed();
        Assert.Single(devices.Withdrawn);
    }

    /// <summary>A link that could not be made is said, in the window, not thrown out of an event handler.</summary>
    [Fact]
    public async Task The_window_says_why_no_link_was_made()
    {
        var devices = new FakeInvitations { Refusal = new InvalidOperationException(RemoteAccessService.NotConnectedForInvite) };
        var window = new AddDeviceViewModel(devices, TimeProvider.System);

        await window.StartAsync(CancellationToken.None);

        Assert.Contains(RemoteAccessService.NotConnectedForInvite, window.Status, StringComparison.Ordinal);
        Assert.False(window.IsLinkShown);
    }

    /// <summary>
    /// Add a device can be pressed only while the computer is connected - past Hello, where an invitation
    /// can be registered - and nothing else of the pane is running; it follows the connection as it comes
    /// and goes.
    /// </summary>
    [Fact]
    public void Add_a_device_is_enabled_only_while_connected()
    {
        var connected = false;
        var pane = new RemoteDevicesViewModel { CanInvite = () => connected };

        pane.RefreshConnection();
        Assert.False(pane.CanAddDevice);

        connected = true;
        pane.RefreshConnection();
        Assert.True(pane.CanAddDevice);

        pane.PaneBusy = true;
        Assert.False(pane.CanAddDevice);
        pane.PaneBusy = false;
        Assert.True(pane.CanAddDevice);

        connected = false;
        pane.RefreshConnection();
        Assert.False(pane.CanAddDevice);
    }

    [Fact]
    public async Task The_trusted_list_shows_revoked_devices_as_revoked()
    {
        using var phone = P256.Generate();
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var pane = new RemoteDevicesViewModel
        {
            Trusted = () => Task.FromResult<IReadOnlyList<TrustedDevice>>(
            [
                new TrustedDevice("phone", P256.PublicRaw(phone), "Phone", KeyAdministration.AddedBy, at, null),
                new TrustedDevice("old", P256.PublicRaw(phone), "Old laptop", "connection code", at, at.AddDays(1))
            ])
        };

        await pane.RefreshAsync();

        Assert.True(pane.HasDevices);
        Assert.Equal(["Phone", "Old laptop"], pane.Devices.Select(d => d.Label));
        Assert.Equal([false, true], pane.Devices.Select(d => d.IsRevoked));
        Assert.StartsWith("added by invitation", pane.Devices[0].Added, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removing a device is asked first, in words that say what it costs - the device reads nothing new and
    /// every other device gets a new key - and only a live device has the button. A no removes nothing; a
    /// yes removes it and reads the list again, so the row says it was revoked.
    /// </summary>
    [Fact]
    public async Task Removing_a_device_asks_first_and_reads_the_list_again()
    {
        using var phone = P256.Generate();
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var reads = 0;
        var answer = false;
        var questions = new List<string>();
        var removed = new List<string>();
        var pane = new RemoteDevicesViewModel
        {
            Trusted = () =>
            {
                reads++;
                return Task.FromResult<IReadOnlyList<TrustedDevice>>(
                [
                    new TrustedDevice("phone", P256.PublicRaw(phone), "Phone", KeyAdministration.AddedBy, at, null),
                    new TrustedDevice("old", P256.PublicRaw(phone), "Old laptop", "connection code", at, at.AddDays(1))
                ]);
            },
            Confirm = question =>
            {
                questions.Add(question);
                return Task.FromResult(answer);
            },
            Remove = deviceId =>
            {
                removed.Add(deviceId);
                return Task.FromResult("Phone was removed on this computer.");
            }
        };
        await pane.RefreshAsync();

        Assert.Equal([true, false], pane.Devices.Select(d => d.CanRemove));

        await pane.RemoveAsync(pane.Devices[1]);
        await pane.RemoveAsync(pane.Devices[0]);

        Assert.Equal(["Phone will not read anything new. Every other device gets a new key. Continue?"], questions);
        Assert.Empty(removed);
        Assert.Equal(1, reads);

        answer = true;
        await pane.RemoveAsync(pane.Devices[0]);

        Assert.Equal(["phone"], removed);
        Assert.Equal(2, reads);
        Assert.False(pane.HasProblem);
        Assert.Equal("Phone was removed on this computer.", pane.Note);
    }

    /// <summary>A removal that could not be made is said under the list, not thrown out of a button.</summary>
    [Fact]
    public async Task A_removal_that_failed_is_said_under_the_list()
    {
        using var phone = P256.Generate();
        var pane = new RemoteDevicesViewModel
        {
            Trusted = () => Task.FromResult<IReadOnlyList<TrustedDevice>>(
                [new TrustedDevice("phone", P256.PublicRaw(phone), "Phone", KeyAdministration.AddedBy, DateTimeOffset.UtcNow, null)]),
            Confirm = _ => Task.FromResult(true),
            Remove = _ => Task.FromException<string>(new InvalidOperationException(RemoteAccessService.CannotRemove))
        };
        await pane.RefreshAsync();

        await pane.RemoveAsync(pane.Devices[0]);

        Assert.True(pane.HasProblem);
        Assert.Contains(RemoteAccessService.CannotRemove, pane.Problem, StringComparison.Ordinal);
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
                using var spare = P256.Generate();
                TrustDevice(keys, "spare", spare, "Spare");
                keys.RevokeAndRotate("spare", tellGateway: true);
            }

            Assert.True(HostKeyStore.HasKeys(store));
            HostKeyStore.Reset(store);
            Assert.False(HostKeyStore.HasKeys(store));
        }

        foreach (var table in new[] { "host_keys", "host_signing", "trusted_devices", "pending_invites", "pending_grants", "owed_revocations" })
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

    /// <summary>A device's answer to an invitation, as the device makes it: its MAC over its own key.</summary>
    private static EnrollmentView Enrolled(InviteLink link, string deviceId, ECDiffieHellman device, string label)
    {
        var publicKey = P256.PublicRaw(device);
        var pairKey = link.PairKey;
        return new EnrollmentView(link.InviteId, deviceId, B64.Url(publicKey), label,
            Enrollment.Mac(pairKey, link.InviteId, deviceId, publicKey));
    }

    /// <summary>The invitations as the window sees them: made at once, and answered when a test says so.</summary>
    private sealed class FakeInvitations : IDeviceInvitations
    {
        public Exception? Refusal { get; init; }

        public InviteLink? Made { get; private set; }

        public List<string> Withdrawn { get; } = [];

        public event Action<AdmittedDevice>? Admitted;

        public event Action<AdmissionNotice>? Noticed;

        public Task<InviteLink> InviteAsync(CancellationToken ct)
        {
            if (Refusal is not null) return Task.FromException<InviteLink>(Refusal);
            Made = new InviteLink(Gateway, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), RandomNumberGenerator.GetBytes(32));
            return Task.FromResult(Made);
        }

        public Task WithdrawAsync(string inviteId)
        {
            Withdrawn.Add(inviteId);
            return Task.CompletedTask;
        }

        public void Admit(AdmittedDevice device) => Admitted?.Invoke(device);

        public void Notice(AdmissionNotice notice) => Noticed?.Invoke(notice);
    }

    private static void TrustDevice(HostKeyStore keys, string deviceId, ECDiffieHellman device, string label)
        => keys.Trust(new TrustedDevice(deviceId, P256.PublicRaw(device), label, "test", DateTimeOffset.UtcNow, null));

    /// <summary>A runner over the key store, as the service makes one: device commands go to the administration.</summary>
    private static RemoteRunner Runner(HostStore store, HostKeyStore keys, Func<KeyAdministration?> administration)
        => new(store, new RemoteApprovals(), new Sealer(keys, TimeProvider.System),
            (_, _, _) => throw new InvalidOperationException("No run expected"), administration);

    private static string[] Labels(IReadOnlyList<AdmittedDevice> admitted) => [.. admitted.Select(d => d.Label)];

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

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
