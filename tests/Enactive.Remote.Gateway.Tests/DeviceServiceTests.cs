namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using MySqlConnector;
using Xunit;

/// <summary>
/// Browser devices: one browser profile of one account, and the public key it will be sent grants for.
///
/// <para>The gateway checks the key's SHAPE and nothing else - it holds no secret and uses the key for
/// nothing. What these test is that a malformed key never gets a row, that every lookup is about the
/// caller's own devices (another person's id answers exactly like a missing one), and that revoking a
/// device takes its grants with it so a removed browser is not left holding keys.</para>
/// </summary>
public sealed class DeviceServiceTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private WebApplicationFactory<Program> _gateway = null!;

    private Database Db => new(database.ConnectionString);

    private DeviceService Devices => new(Db, Limits.Unlimited, TimeProvider.System);

    public Task InitializeAsync()
    {
        _gateway = TestGateway.Create(database);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _gateway.DisposeAsync().AsTask();

    private static string Name(string stem) => stem + "-" + Guid.NewGuid().ToString("N")[..8];

    private Task<UserAccess> PersonAsync(string stem) => TestAccounts.CreateAsync(database, Name(stem));

    /// <summary>The raw public half of a fresh P-256 key, as a browser's WebCrypto exports it.</summary>
    private static byte[] NewPublicKey()
    {
        using var key = P256.Generate();
        return P256.PublicRaw(key);
    }

    private static string KeyText(byte[] key) => B64.Url(key);

    /// <summary>An uncompressed point whose coordinates are not on the curve: the right shape, the wrong maths.</summary>
    private static byte[] OffCurveKey()
    {
        var key = NewPublicKey();
        key[64] ^= 0x01;
        return key;
    }

    // ── registering ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_device_registers_with_a_valid_public_key()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var key = NewPublicKey();

        var created = await browser.PostAsync<JsonElement>(
            "/api/devices", new { publicKey = KeyText(key), label = "  Alice laptop  " });
        var id = created.GetProperty("id").GetString()!;

        var listed = await browser.GetAsync<JsonElement>("/api/devices");
        var device = Assert.Single(listed.EnumerateArray());

        Assert.Equal(id, device.GetProperty("id").GetString());
        Assert.Equal("Alice laptop", device.GetProperty("label").GetString());
        Assert.Equal(KeyText(key), device.GetProperty("publicKey").GetString());
        Assert.False(device.GetProperty("revoked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, device.GetProperty("lastSeenAt").ValueKind);
        Assert.True(device.TryGetProperty("createdAt", out _));

        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM audit WHERE owner_id = '{browser.UserId}' AND actor = 'user:{browser.UserId}' "
            + $"AND action = 'device-registered' AND target = '{id}'"));
    }

    [Fact]
    public async Task A_point_off_the_curve_is_refused()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));

        await AssertRefusedAsync(browser, KeyText(OffCurveKey()), "bad-key");

        Assert.Empty((await browser.GetAsync<JsonElement>("/api/devices")).EnumerateArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64url!")]
    [InlineData("AAAA")]
    public async Task A_key_of_the_wrong_shape_is_refused(string text)
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));

        await AssertRefusedAsync(browser, text, "bad-key");
    }

    [Fact]
    public async Task A_compressed_key_is_refused()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var compressed = NewPublicKey()[..33];
        compressed[0] = 0x02;

        await AssertRefusedAsync(browser, KeyText(compressed), "bad-key");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tab\there")]
    [InlineData("new\nline")]
    public async Task A_label_that_is_empty_or_holds_control_characters_is_refused(string label)
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));

        using var response = await browser.SendAsync(
            HttpMethod.Post, "/api/devices", new { publicKey = KeyText(NewPublicKey()), label });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad-request", (await ErrorAsync(response)).Code);
    }

    [Fact]
    public async Task A_label_longer_than_the_column_is_refused_not_cut()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));

        using var response = await browser.SendAsync(
            HttpMethod.Post, "/api/devices", new { publicKey = KeyText(NewPublicKey()), label = new string('x', 81) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_person_may_hold_only_as_many_devices_as_the_limit_allows()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var devices = new DeviceService(Db, Limits.Unlimited with { DevicesPerUser = 2 }, TimeProvider.System);

        var first = await devices.RegisterAsync(alice, NewPublicKey(), "one", default);
        await devices.RegisterAsync(alice, NewPublicKey(), "two", default);

        var refused = await Assert.ThrowsAsync<GatewayFault>(
            () => devices.RegisterAsync(alice, NewPublicKey(), "three", default));
        Assert.Equal("device-limit", refused.Code);
        Assert.Equal(409, refused.Status);

        // Another account has its own allowance, and a removed device frees one of the first's.
        await devices.RegisterAsync(bob, NewPublicKey(), "bobs", default);
        await devices.RevokeAsync(alice, first, default);
        await devices.RegisterAsync(alice, NewPublicKey(), "three", default);
    }

    /// <summary>
    /// Two registrations racing for the last place must not both get it: the count is read under a lock,
    /// so the second one waits for the first's insert and then sees it.
    /// </summary>
    [Fact]
    public async Task Concurrent_registrations_cannot_pass_the_limit_together()
    {
        var alice = await PersonAsync("alice");
        var devices = new DeviceService(Db, Limits.Unlimited with { DevicesPerUser = 3 }, TimeProvider.System);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async n =>
        {
            try
            {
                await devices.RegisterAsync(alice, NewPublicKey(), $"d{n}", default);
                return true;
            }
            catch (GatewayFault)
            {
                return false;
            }
        }));

        Assert.Equal(3, attempts.Count(ok => ok));
        Assert.Equal(3, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM devices WHERE owner_id = '{alice.UserId}'"));
    }

    // ── listing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_person_sees_only_their_own_devices()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var alices = await Devices.RegisterAsync(alice, NewPublicKey(), "alices", default);
        await Devices.RegisterAsync(bob, NewPublicKey(), "bobs", default);

        var seen = await Devices.ListAsync(alice, default);

        Assert.Equal(alices, Assert.Single(seen).Id);
    }

    // ── device-bound calls ──────────────────────────────────────────────────

    [Fact]
    public async Task A_live_device_is_resolved_and_its_visit_is_recorded()
    {
        var alice = await PersonAsync("alice");
        var id = await Devices.RegisterAsync(alice, NewPublicKey(), "laptop", default);

        var access = await Devices.RequireAsync(alice, id, default);

        Assert.Equal(new DeviceAccess(id, alice.UserId), access);
        Assert.NotNull(Assert.Single(await Devices.ListAsync(alice, default)).LastSeenAt);
    }

    [Fact]
    public async Task A_revoked_device_is_refused_on_device_calls()
    {
        var alice = await PersonAsync("alice");
        var id = await Devices.RegisterAsync(alice, NewPublicKey(), "laptop", default);
        await Devices.RevokeAsync(alice, id, default);

        var refused = await Assert.ThrowsAsync<GatewayFault>(() => Devices.RequireAsync(alice, id, default));

        Assert.Equal("device-revoked", refused.Code);
        Assert.Equal(403, refused.Status);
        Assert.True(Assert.Single(await Devices.ListAsync(alice, default)).Revoked);
    }

    /// <summary>
    /// A revoked device that calls again must not look as if it were still in use: the visit is only
    /// recorded for a device that was let in.
    /// </summary>
    [Fact]
    public async Task A_refused_call_does_not_record_a_visit()
    {
        var alice = await PersonAsync("alice");
        var id = await Devices.RegisterAsync(alice, NewPublicKey(), "laptop", default);
        await Devices.RevokeAsync(alice, id, default);

        await Assert.ThrowsAsync<GatewayFault>(() => Devices.RequireAsync(alice, id, default));

        Assert.Null(Assert.Single(await Devices.ListAsync(alice, default)).LastSeenAt);
    }

    [Fact]
    public async Task Another_persons_device_and_a_missing_one_are_refused_in_the_same_words()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var alices = await Devices.RegisterAsync(alice, NewPublicKey(), "laptop", default);

        var foreign = await Assert.ThrowsAsync<GatewayFault>(() => Devices.RequireAsync(bob, alices, default));
        var missing = await Assert.ThrowsAsync<GatewayFault>(() => Devices.RequireAsync(bob, Ids.New(), default));

        Assert.Equal(404, foreign.Status);
        Assert.Equal((missing.Code, missing.Status, missing.Message), (foreign.Code, foreign.Status, foreign.Message));
        Assert.Null(Assert.Single(await Devices.ListAsync(alice, default)).LastSeenAt);
    }

    // ── revoking ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Revoking_a_device_deletes_its_grants()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(browser.UserId, "unused");
        var kept = await Devices.RegisterAsync(owner, NewPublicKey(), "kept", default);
        var removed = (await browser.PostAsync<JsonElement>(
            "/api/devices", new { publicKey = KeyText(NewPublicKey()), label = "removed" }))
            .GetProperty("id").GetString()!;
        var (hostId, _, _) = await new UserService(Db, Limits.Unlimited, TimeProvider.System)
            .RegisterHostAsync(owner, "Studio PC", default);

        foreach (var device in new[] { kept, removed })
        {
            await database.ExecuteAsync(
                """
                INSERT INTO grants (owner_id, host_id, device_id, epoch, grant_json, created_at)
                VALUES (@owner, @host, @device, 1, '{}', UTC_TIMESTAMP(3))
                """,
                ("@owner", owner.UserId), ("@host", hostId), ("@device", device));
        }

        using var response = await browser.SendAsync(HttpMethod.Post, $"/api/devices/{removed}/revoke");
        response.EnsureSuccessStatusCode();

        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM grants WHERE device_id = '{removed}'"));
        Assert.Equal(1, await database.ScalarLongAsync($"SELECT COUNT(*) FROM grants WHERE device_id = '{kept}'"));
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM audit WHERE owner_id = '{owner.UserId}' AND actor = 'user:{owner.UserId}' "
            + $"AND action = 'device-revoked' AND target = '{removed}'"));
    }

    [Fact]
    public async Task Revoking_a_device_twice_is_a_success_that_audits_once()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var id = (await browser.PostAsync<JsonElement>(
            "/api/devices", new { publicKey = KeyText(NewPublicKey()), label = "laptop" }))
            .GetProperty("id").GetString()!;

        await browser.PostAsync($"/api/devices/{id}/revoke", new { });
        await browser.PostAsync($"/api/devices/{id}/revoke", new { });

        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM audit WHERE action = 'device-revoked' AND target = '{id}'"));
    }

    [Fact]
    public async Task Bob_cannot_revoke_alices_device()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        using var bob = await PanelClient.SignedInAsync(_gateway, Name("bob"));
        var id = (await alice.PostAsync<JsonElement>(
            "/api/devices", new { publicKey = KeyText(NewPublicKey()), label = "laptop" }))
            .GetProperty("id").GetString()!;

        using var foreign = await bob.SendAsync(HttpMethod.Post, $"/api/devices/{id}/revoke");
        using var missing = await bob.SendAsync(HttpMethod.Post, $"/api/devices/{Ids.New()}/revoke");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await foreign.Content.ReadAsStringAsync(), await missing.Content.ReadAsStringAsync());
        Assert.False(Assert.Single((await alice.GetAsync<JsonElement>("/api/devices")).EnumerateArray())
            .GetProperty("revoked").GetBoolean());
    }

    [Fact]
    public void The_device_header_names_the_device_or_nothing()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        Assert.Null(context.DeviceIdOrNull());

        context.Request.Headers["X-Enactive-Device"] = "  abc123  ";
        Assert.Equal("abc123", context.DeviceIdOrNull());

        context.Request.Headers["X-Enactive-Device"] = "   ";
        Assert.Null(context.DeviceIdOrNull());
    }

    [Fact]
    public void A_device_bound_call_without_the_header_is_refused_with_one_code()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();

        var refused = Assert.Throws<GatewayFault>(() => context.RequireDeviceId());
        Assert.Equal("device-header", refused.Code);
        Assert.Equal(400, refused.Status);

        context.Request.Headers["X-Enactive-Device"] = " abc123 ";
        Assert.Equal("abc123", context.RequireDeviceId());
    }

    /// <summary>
    /// A grant written by a request that was already past its own check when the device was revoked can
    /// land after the revoke. Revoking again must sweep it rather than return early.
    /// </summary>
    [Fact]
    public async Task Revoking_an_already_revoked_device_still_deletes_a_grant_left_behind()
    {
        var alice = await PersonAsync("alice");
        var id = await Devices.RegisterAsync(alice, NewPublicKey(), "laptop", default);
        var (hostId, _, _) = await new UserService(Db, Limits.Unlimited, TimeProvider.System)
            .RegisterHostAsync(alice, "Studio PC", default);
        await Devices.RevokeAsync(alice, id, default);

        await database.ExecuteAsync(
            """
            INSERT INTO grants (owner_id, host_id, device_id, epoch, grant_json, created_at)
            VALUES (@owner, @host, @device, 1, '{}', UTC_TIMESTAMP(3))
            """,
            ("@owner", alice.UserId), ("@host", hostId), ("@device", id));

        await Devices.RevokeAsync(alice, id, default);

        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM grants WHERE device_id = '{id}'"));
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM audit WHERE action = 'device-revoked' AND target = '{id}'"));
    }

    /// <summary>An account deleted since its session was checked is refused cleanly, not with a foreign-key 500.</summary>
    [Fact]
    public async Task Registering_for_an_account_that_is_gone_is_refused_cleanly()
    {
        var refused = await Assert.ThrowsAsync<GatewayFault>(
            () => Devices.RegisterAsync(new UserAccess(Ids.New(), "gone"), NewPublicKey(), "laptop", default));

        Assert.Equal("unauthenticated", refused.Code);
        Assert.Equal(401, refused.Status);
    }

    [Theory]
    [InlineData(1, "already has 1 device.")]
    [InlineData(2, "already has 2 devices.")]
    public void The_limit_message_counts_in_the_right_number(int max, string expected)
        => Assert.Contains(expected, GatewayFault.DeviceLimit(max).Message);

    // ── grants ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_computer_publishes_a_grant_for_its_owners_device()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(browser.UserId, "unused");
        var device = await DeviceAsync(owner);
        var host = await ComputerAsync(owner);
        using var signer = P256.GenerateSigning();
        using var limit = new HostCallLimit();
        var grant = Paired(host.HostId, device, 1, signer);

        var reply = await Hub(host, limit).PublishGrants([grant]);

        Assert.Null(reply.Fault);
        var listed = Assert.Single(await GrantsOfAsync(browser, device.Id));
        Assert.Equal(host.HostId, listed.HostId);
        Assert.Equal(1u, listed.KeyEpoch);
        Assert.Equal(grant, Assert.Single(listed.Grants));

        // The first grant stored for a computer pins its signing key.
        Assert.Equal(P256.SigningPublicRaw(signer), await SigningPublicAsync(host.HostId));
    }

    [Fact]
    public async Task A_grant_for_another_owners_device_is_refused()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(alice.UserId, "unused");
        var own = await DeviceAsync(owner);
        var host = await ComputerAsync(owner);
        var bobs = await DeviceAsync(await PersonAsync("bob"));
        var missing = (Ids.New(), NewPublicKey());
        using var signer = P256.GenerateSigning();
        using var limit = new HostCallLimit();
        var hub = Hub(host, limit);

        var foreign = (await hub.PublishGrants([Paired(host.HostId, bobs, 1, signer)])).Fault!;
        var absent = (await hub.PublishGrants([Paired(host.HostId, missing, 1, signer)])).Fault!;

        Assert.Equal("not-found", foreign.Code);
        Assert.Equal((absent.Code, absent.Message), (foreign.Code, foreign.Message));
        Assert.Equal(0, await GrantCountAsync(host.HostId));
        Assert.Null(await SigningPublicAsync(host.HostId));

        // A browser of Alice's, once the computer has granted her a key, is refused in the same words as
        // for a device that does not exist.
        Assert.Null((await hub.PublishGrants([Paired(host.HostId, own, 1, signer)])).Fault);
        using var foreignPost = await PostGrantsAsync(alice, own.Id, Paired(host.HostId, bobs, 1, signer, Ids.New()));
        using var absentPost = await PostGrantsAsync(alice, own.Id, Paired(host.HostId, missing, 1, signer, Ids.New()));

        Assert.Equal(HttpStatusCode.NotFound, foreignPost.StatusCode);
        Assert.Equal(await absentPost.Content.ReadAsStringAsync(), await foreignPost.Content.ReadAsStringAsync());
        Assert.Equal(1, await GrantCountAsync(host.HostId));
    }

    [Fact]
    public async Task A_grant_for_a_revoked_device_is_refused()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(alice.UserId, "unused");
        var own = await DeviceAsync(owner, "laptop");
        var removed = await DeviceAsync(owner, "phone");
        var host = await ComputerAsync(owner);
        using var signer = P256.GenerateSigning();
        using var limit = new HostCallLimit();
        await Devices.RevokeAsync(owner, removed.Id, default);

        var hub = Hub(host, limit);

        var fault = (await hub.PublishGrants([Paired(host.HostId, removed, 1, signer)])).Fault!;
        Assert.Null((await hub.PublishGrants([Paired(host.HostId, own, 1, signer)])).Fault);
        using var posted = await PostGrantsAsync(alice, own.Id, Paired(host.HostId, removed, 1, signer, Ids.New()));

        Assert.Equal("not-found", fault.Code);
        Assert.Equal(HttpStatusCode.NotFound, posted.StatusCode);
        Assert.Equal(1, await GrantCountAsync(host.HostId));
    }

    /// <summary>
    /// A revocation that has locked the device and not yet committed, and a grant to that device arriving
    /// meanwhile. The grant must wait for it and then be refused: read without a lock, the device still
    /// looked live and the grant was stored while the revocation was under way, for a device being removed.
    /// </summary>
    [Fact]
    public async Task A_grant_racing_the_revocation_of_its_device_does_not_survive_it()
    {
        var owner = await PersonAsync("alice");
        var device = await DeviceAsync(owner);
        var host = await ComputerAsync(owner);
        using var signer = P256.GenerateSigning();

        await using var revoker = await database.OpenAsync();
        await using var revocation = await revoker.BeginTransactionAsync();
        await using (var revoke = new MySqlCommand(
            "UPDATE devices SET revoked_at = UTC_TIMESTAMP(3) WHERE owner_id = @owner AND id = @device",
            revoker, revocation))
        {
            revoke.Parameters.AddWithValue("@owner", owner.UserId);
            revoke.Parameters.AddWithValue("@device", device.Id);
            await revoke.ExecuteNonQueryAsync();
        }

        var publishing = Devices.PublishGrantsAsync(host, [Paired(host.HostId, device, 1, signer)], default);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(publishing.IsCompleted);

        await revocation.CommitAsync();

        var refused = await Assert.ThrowsAsync<GatewayFault>(() => publishing);
        Assert.Equal("not-found", refused.Code);
        Assert.Equal(0, await GrantCountAsync(host.HostId));
    }

    [Fact]
    public async Task A_device_reads_only_its_own_grants()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(alice.UserId, "unused");
        var laptop = await DeviceAsync(owner, "laptop");
        var phone = await DeviceAsync(owner, "phone");
        var studio = await ComputerAsync(owner);
        var old = await ComputerAsync(owner);
        using var studioSigner = P256.GenerateSigning();
        using var oldSigner = P256.GenerateSigning();

        await Devices.PublishGrantsAsync(studio,
            [Paired(studio.HostId, laptop, 1, studioSigner), Paired(studio.HostId, phone, 1, studioSigner)], default);
        await Devices.PublishGrantsAsync(old, [Paired(old.HostId, laptop, 1, oldSigner)], default);
        await Users.RevokeHostAsync(owner, old.HostId, default);

        // The phone's grant is not the laptop's, and a revoked computer's keys are not handed out at all.
        var seen = Assert.Single(await GrantsOfAsync(alice, laptop.Id));
        Assert.Equal(studio.HostId, seen.HostId);
        Assert.Equal(laptop.Id, Assert.Single(seen.Grants).DeviceId);
        Assert.Equal(phone.Id, Assert.Single(Assert.Single(await GrantsOfAsync(alice, phone.Id)).Grants).DeviceId);

        // Bob naming Alice's device is refused in the words used for one that does not exist, and his own
        // device has nothing of hers.
        using var bob = await PanelClient.SignedInAsync(_gateway, Name("bob"));
        var bobs = await DeviceAsync(new UserAccess(bob.UserId, "unused"));
        using var foreign = await ReadGrantsAsync(bob, laptop.Id);
        using var missing = await ReadGrantsAsync(bob, Ids.New());

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await foreign.Content.ReadAsStringAsync());
        Assert.Empty(await GrantsOfAsync(bob, bobs.Id));

        using var unnamed = await ReadGrantsAsync(alice, null);
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
        Assert.Equal("device-header", (await ErrorAsync(unnamed)).Code);
    }

    [Theory]
    [InlineData("epoch", "zero")]
    [InlineData("ephemeralPublic", "short")]
    [InlineData("ephemeralPublic", "off-curve")]
    [InlineData("nonce", "short")]
    [InlineData("ciphertext", "short")]
    [InlineData("mac", "signature-length")]
    [InlineData("mac", "not-base64url")]
    [InlineData("authBy", "uppercase")]
    [InlineData("authBy", "trailing-newline")]
    [InlineData("authBy", "unknown")]
    [InlineData("hostSigningPublic", "short")]
    [InlineData("hostSigningPublic", "off-curve")]
    [InlineData("hostSigningPublic", "padded")]
    [InlineData("hostId", "missing")]
    [InlineData("deviceId", "missing")]
    public async Task A_malformed_grant_is_refused(string field, string variant)
    {
        var owner = await PersonAsync("alice");
        var device = await DeviceAsync(owner);
        var host = await ComputerAsync(owner);
        using var signer = P256.GenerateSigning();
        var good = Paired(host.HostId, device, 1, signer);
        var bad = Malformed(Paired(host.HostId, device, 2, signer), field, variant);

        var refused = await Assert.ThrowsAsync<GatewayFault>(
            () => Devices.PublishGrantsAsync(host, [good, bad], default));

        Assert.Equal("bad-grant", refused.Code);
        Assert.Equal(400, refused.Status);
        Assert.Contains($"'{field}'", refused.Message);

        // One bad grant refuses the whole batch: the good one beside it is not stored either.
        Assert.Equal(0, await GrantCountAsync(host.HostId));
    }

    /// <summary>
    /// A grant the computer signed carries a 64-byte signature where a paired one carries a 32-byte HMAC.
    /// One length for both would refuse every rotation grant, or let a truncated signature through.
    /// </summary>
    [Fact]
    public async Task A_signed_grant_carries_a_signature_and_a_paired_one_an_hmac()
    {
        var owner = await PersonAsync("alice");
        var device = await DeviceAsync(owner);
        var host = await ComputerAsync(owner);
        using var signer = P256.GenerateSigning();
        var signed = Signed(host.HostId, device, 1, signer);

        var refused = await Assert.ThrowsAsync<GatewayFault>(() => Devices.PublishGrantsAsync(
            host, [signed with { Mac = B64.Url(new byte[32]) }], default));
        await Devices.PublishGrantsAsync(host, [signed], default);

        Assert.Contains("'mac'", refused.Message);
        Assert.Equal(1, await GrantCountAsync(host.HostId));
    }

    [Fact]
    public async Task More_than_fifty_grants_in_one_call_are_refused()
    {
        var owner = await PersonAsync("alice");
        var device = await DeviceAsync(owner);
        var host = await ComputerAsync(owner);
        using var signer = P256.GenerateSigning();
        var grants = Enumerable.Range(1, 51).Select(epoch => Paired(host.HostId, device, (uint)epoch, signer)).ToList();

        var refused = await Assert.ThrowsAsync<GatewayFault>(() => Devices.PublishGrantsAsync(host, grants, default));
        await Devices.PublishGrantsAsync(host, grants[..50], default);

        Assert.Equal("bad-grant", refused.Code);
        Assert.Equal(50, await GrantCountAsync(host.HostId));
    }

    [Fact]
    public async Task A_computer_grants_only_its_own_keys()
    {
        var owner = await PersonAsync("alice");
        var device = await DeviceAsync(owner);
        var studio = await ComputerAsync(owner);
        var laptop = await ComputerAsync(owner);
        using var signer = P256.GenerateSigning();
        using var limit = new HostCallLimit();

        var fault = (await Hub(studio, limit).PublishGrants([Paired(laptop.HostId, device, 1, signer)])).Fault!;

        Assert.Equal("bad-grant", fault.Code);
        Assert.Contains("'hostId'", fault.Message);
        Assert.Equal(0, await GrantCountAsync(laptop.HostId));
    }

    [Fact]
    public async Task A_revoked_computer_cannot_publish_grants()
    {
        var owner = await PersonAsync("alice");
        var device = await DeviceAsync(owner);
        var host = await ComputerAsync(owner);
        using var signer = P256.GenerateSigning();
        using var limit = new HostCallLimit();
        await Users.RevokeHostAsync(owner, host.HostId, default);

        var fault = (await Hub(host, limit).PublishGrants([Paired(host.HostId, device, 1, signer)])).Fault!;

        Assert.Equal(FaultCode.HostRevoked, fault.Code);
        Assert.Equal(0, await GrantCountAsync(host.HostId));
    }

    /// <summary>
    /// A trusted browser answers an invitation with grants authenticated by that invitation's pair key. It
    /// never makes the grant that answers a computer's own pairing, nor one the computer signed.
    /// </summary>
    [Fact]
    public async Task A_browser_grants_its_keys_only_under_an_invitations_pair_key()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(alice.UserId, "unused");
        var laptop = await DeviceAsync(owner, "laptop");
        var phone = await DeviceAsync(owner, "phone");
        var host = await ComputerAsync(owner);
        using var signer = P256.GenerateSigning();
        var invited = Paired(host.HostId, phone, 1, signer, Ids.New());
        await Devices.PublishGrantsAsync(
            host, [Paired(host.HostId, laptop, 1, signer), Signed(host.HostId, laptop, 2, signer)], default);

        using var accepted = await PostGrantsAsync(alice, laptop.Id, invited);
        using var connect = await PostGrantsAsync(alice, laptop.Id, Paired(host.HostId, phone, 2, signer));
        using var signed = await PostGrantsAsync(alice, laptop.Id, Signed(host.HostId, phone, 2, signer));

        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
        Assert.Equal(invited, Assert.Single(Assert.Single(await GrantsOfAsync(alice, phone.Id)).Grants));

        foreach (var response in new[] { connect, signed })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await ErrorAsync(response);
            Assert.Equal("bad-grant", error.Code);
            Assert.Contains("'authBy'", error.Error);
        }
    }

    [Fact]
    public async Task A_browser_grants_keys_only_of_its_persons_live_computers()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(alice.UserId, "unused");
        var laptop = await DeviceAsync(owner, "laptop");
        var phone = await DeviceAsync(owner, "phone");
        var revoked = await ComputerAsync(owner);
        var bobs = await ComputerAsync(await PersonAsync("bob"));
        using var signer = P256.GenerateSigning();
        await Users.RevokeHostAsync(owner, revoked.HostId, default);

        using var foreign = await PostGrantsAsync(alice, laptop.Id, Paired(bobs.HostId, phone, 1, signer, Ids.New()));
        using var missing = await PostGrantsAsync(alice, laptop.Id, Paired(Ids.New(), phone, 1, signer, Ids.New()));
        using var gone = await PostGrantsAsync(alice, laptop.Id, Paired(revoked.HostId, phone, 1, signer, Ids.New()));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await foreign.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(0, await GrantCountAsync(bobs.HostId));
        Assert.Equal(0, await GrantCountAsync(revoked.HostId));
    }

    [Fact]
    public async Task Key_epoch_follows_the_newest_grant()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(alice.UserId, "unused");
        var device = await DeviceAsync(owner);
        var host = await ComputerAsync(owner);
        using var signer = P256.GenerateSigning();

        await Devices.PublishGrantsAsync(host, [Paired(host.HostId, device, 1, signer)], default);
        Assert.Equal(1, await KeyEpochAsync(host.HostId));

        await Devices.PublishGrantsAsync(host, [Signed(host.HostId, device, 3, signer)], default);
        Assert.Equal(3, await KeyEpochAsync(host.HostId));

        // An older grant sent late does not take it back.
        await Devices.PublishGrantsAsync(host, [Signed(host.HostId, device, 2, signer)], default);
        Assert.Equal(3, await KeyEpochAsync(host.HostId));

        // A browser passes on keys the computer made, and a key newer than the computer's newest is not one.
        using var posted = await PostGrantsAsync(alice, device.Id, Paired(host.HostId, device, 7, signer, Ids.New()));
        Assert.Equal(HttpStatusCode.BadRequest, posted.StatusCode);
        var error = await ErrorAsync(posted);
        Assert.Equal("bad-grant", error.Code);
        Assert.Contains("'epoch'", error.Error);
        Assert.Equal(3, await KeyEpochAsync(host.HostId));

        var listed = Assert.Single(await GrantsOfAsync(alice, device.Id));
        Assert.Equal(3u, listed.KeyEpoch);
        Assert.Equal([1u, 2u, 3u], listed.Grants.Select(grant => grant.Epoch));
    }

    /// <summary>
    /// Only the computer pins its signing key. A browser's grant stored first pinned whatever key it carried,
    /// so any signed-in session could lock a computer that had not paired yet out of its own pairing.
    /// </summary>
    [Fact]
    public async Task A_browser_cannot_grant_a_key_of_a_computer_that_has_granted_none()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(alice.UserId, "unused");
        var laptop = await DeviceAsync(owner, "laptop");
        var host = await ComputerAsync(owner);
        using var computer = P256.GenerateSigning();
        using var impostor = P256.GenerateSigning();

        using var first = await PostGrantsAsync(alice, laptop.Id, Paired(host.HostId, laptop, 1, impostor, Ids.New()));

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        var error = await ErrorAsync(first);
        Assert.Equal("bad-grant", error.Code);
        Assert.Contains("has not granted any key yet", error.Error);
        Assert.Null(await SigningPublicAsync(host.HostId));
        Assert.Equal(0, await GrantCountAsync(host.HostId));

        // The computer's own first grant still goes through and pins its key, and a browser's grant carrying
        // another key is refused from then on.
        await Devices.PublishGrantsAsync(host, [Paired(host.HostId, laptop, 1, computer)], default);
        Assert.Equal(P256.SigningPublicRaw(computer), await SigningPublicAsync(host.HostId));

        var phone = await DeviceAsync(owner, "phone");
        using var other = await PostGrantsAsync(alice, laptop.Id, Paired(host.HostId, phone, 1, impostor, Ids.New()));

        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Contains("'hostSigningPublic'", (await ErrorAsync(other)).Error);
        Assert.Equal(1, await GrantCountAsync(host.HostId));
    }

    /// <summary>
    /// A browser never replaces a grant already stored: the computer's grant for that device and epoch would
    /// be swapped for one of the browser's making, and a device that cannot open it loses the key it had.
    /// The computer replacing its own is the re-sent rotation, and stays allowed.
    /// </summary>
    [Fact]
    public async Task A_browser_grant_never_replaces_one_already_stored()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(alice.UserId, "unused");
        var laptop = await DeviceAsync(owner, "laptop");
        var phone = await DeviceAsync(owner, "phone");
        var host = await ComputerAsync(owner);
        using var signer = P256.GenerateSigning();
        var computers = Paired(host.HostId, phone, 1, signer);
        await Devices.PublishGrantsAsync(host, [Paired(host.HostId, laptop, 1, signer), computers], default);

        using var posted = await PostGrantsAsync(alice, laptop.Id, Paired(host.HostId, phone, 1, signer, Ids.New()));

        Assert.Equal(HttpStatusCode.Conflict, posted.StatusCode);
        Assert.Equal("conflict", (await ErrorAsync(posted)).Code);
        Assert.Equal(computers, Assert.Single(Assert.Single(await GrantsOfAsync(alice, phone.Id)).Grants));
    }

    /// <summary>
    /// Ids as the service issues them and nothing else. The id columns ignore trailing spaces, so a computer
    /// named with one was found and the grant stored, naming an id no device asks for; an id in capitals
    /// was refused as a missing one, where it is a malformed grant.
    /// </summary>
    [Theory]
    [InlineData("hostId", "trailing-space")]
    [InlineData("hostId", "uppercase")]
    [InlineData("deviceId", "trailing-space")]
    [InlineData("deviceId", "uppercase")]
    public async Task A_grant_naming_an_id_not_as_issued_is_refused(string field, string variant)
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(alice.UserId, "unused");
        var laptop = await DeviceAsync(owner, "laptop");
        var phone = await DeviceAsync(owner, "phone");
        var host = await ComputerAsync(owner);
        using var signer = P256.GenerateSigning();
        await Devices.PublishGrantsAsync(host, [Paired(host.HostId, laptop, 1, signer)], default);
        var grant = Paired(host.HostId, phone, 1, signer, Ids.New());
        string Spelled(string id) => variant == "uppercase" ? id.ToUpperInvariant() : id + " ";

        using var posted = await PostGrantsAsync(alice, laptop.Id, field == "hostId"
            ? grant with { HostId = Spelled(grant.HostId) }
            : grant with { DeviceId = Spelled(grant.DeviceId) });

        Assert.Equal(HttpStatusCode.BadRequest, posted.StatusCode);
        var error = await ErrorAsync(posted);
        Assert.Equal("bad-grant", error.Code);
        Assert.Contains($"'{field}'", error.Error);
        Assert.Equal(1, await GrantCountAsync(host.HostId));
        Assert.Equal(P256.SigningPublicRaw(signer), await SigningPublicAsync(host.HostId));
    }

    [Fact]
    public async Task A_grant_carrying_another_signing_key_than_the_first_is_refused()
    {
        var owner = await PersonAsync("alice");
        var device = await DeviceAsync(owner);
        var host = await ComputerAsync(owner);
        using var pinned = P256.GenerateSigning();
        using var other = P256.GenerateSigning();
        await Devices.PublishGrantsAsync(host, [Paired(host.HostId, device, 1, pinned)], default);

        var refused = await Assert.ThrowsAsync<GatewayFault>(
            () => Devices.PublishGrantsAsync(host, [Signed(host.HostId, device, 2, other)], default));

        Assert.Equal("bad-grant", refused.Code);
        Assert.Contains("'hostSigningPublic'", refused.Message);
        Assert.Contains("earlier grants", refused.Message);
        Assert.Equal(1, await GrantCountAsync(host.HostId));
        Assert.Equal(P256.SigningPublicRaw(pinned), await SigningPublicAsync(host.HostId));

        // Two keys in a computer's first batch: neither is pinned, because nothing of the batch is kept.
        var fresh = await ComputerAsync(owner);
        await Assert.ThrowsAsync<GatewayFault>(() => Devices.PublishGrantsAsync(fresh,
            [Paired(fresh.HostId, device, 1, pinned), Paired(fresh.HostId, device, 2, other)], default));
        Assert.Null(await SigningPublicAsync(fresh.HostId));
    }

    /// <summary>A rotation can be sent again, and the last one sent is the one the device needs.</summary>
    [Fact]
    public async Task A_grant_sent_again_replaces_the_earlier_one()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(alice.UserId, "unused");
        var device = await DeviceAsync(owner);
        var host = await ComputerAsync(owner);
        using var signer = P256.GenerateSigning();
        var later = Signed(host.HostId, device, 1, signer);

        await Devices.PublishGrantsAsync(host, [Signed(host.HostId, device, 1, signer)], default);
        await Devices.PublishGrantsAsync(host, [later], default);

        Assert.Equal(later, Assert.Single(Assert.Single(await GrantsOfAsync(alice, device.Id)).Grants));
    }

    /// <summary>
    /// The panel asks for its grants often, and each device-bound call wrote the device's row. A visit
    /// within a minute of the last one recorded is not written again.
    /// </summary>
    [Fact]
    public async Task A_devices_visit_is_recorded_at_most_once_a_minute()
    {
        var owner = await PersonAsync("alice");
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
        var devices = new DeviceService(Db, Limits.Unlimited, clock);
        var id = await devices.RegisterAsync(owner, NewPublicKey(), "laptop", default);
        var first = clock.Now;

        await devices.RequireAsync(owner, id, default);
        clock.Now = first.AddSeconds(30);
        await devices.RequireAsync(owner, id, default);
        Assert.Equal(first, Assert.Single(await devices.ListAsync(owner, default)).LastSeenAt);

        clock.Now = first.AddSeconds(61);
        await devices.RequireAsync(owner, id, default);
        Assert.Equal(first.AddSeconds(61), Assert.Single(await devices.ListAsync(owner, default)).LastSeenAt);
    }

    // ── invitations ─────────────────────────────────────────────────────────

    /// <summary>
    /// Two new devices answering one invitation at the same moment: the invite row is read under a lock,
    /// so the second waits for the first and then finds the invite used. Without the lock both read it
    /// unused and both enrolled, and the inviter would be shown whichever it read first.
    /// </summary>
    [Fact]
    public async Task An_invite_is_used_once()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(alice.UserId, "unused");
        var laptop = await DeviceAsync(owner, "laptop");
        var contenders = new[]
        {
            await DeviceAsync(owner, "phone"), await DeviceAsync(owner, "tablet"),
            await DeviceAsync(owner, "desk"), await DeviceAsync(owner, "tv")
        };
        var pairKey = RandomNumberGenerator.GetBytes(32);
        var invite = Ids.New();

        using (var created = await PostInviteAsync(alice, laptop.Id, invite))
        {
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        }

        using (var none = await ReadEnrollmentAsync(alice, laptop.Id, invite))
        {
            Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
        }

        var attempts = await Task.WhenAll(contenders.Select(async device =>
        {
            try
            {
                await Devices.EnrollAsync(
                    owner, invite, device.Id, Enrollment.Mac(pairKey, invite, device.Id, device.Key), default);
                return (Device: device, Fault: (GatewayFault?)null);
            }
            catch (GatewayFault fault)
            {
                return (Device: device, Fault: fault);
            }
        }));

        var winner = Assert.Single(attempts, attempt => attempt.Fault is null).Device;
        Assert.All(attempts.Where(attempt => attempt.Fault is not null), attempt =>
        {
            Assert.Equal("invite-used", attempt.Fault!.Code);
            Assert.Equal(409, attempt.Fault.Status);
        });
        Assert.Equal(1, await database.ScalarLongAsync($"SELECT COUNT(*) FROM enrollments WHERE invite_id = '{invite}'"));
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM invites WHERE id = '{invite}' AND consumed_at IS NOT NULL"));

        // The browser that made the invitation is shown the winner, with the key from the winner's own
        // row - and the MAC the new device made over it verifies with the pair key.
        using var read = await ReadEnrollmentAsync(alice, laptop.Id, invite);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var enrollment = await read.Content.ReadFromJsonAsync<JsonElement>(RemoteJson.Options);
        Assert.Equal(winner.Id, enrollment.GetProperty("deviceId").GetString());
        Assert.Equal(B64.Url(winner.Key), enrollment.GetProperty("publicKey").GetString());
        Assert.Equal(new[] { "phone", "tablet", "desk", "tv" }[Array.IndexOf(contenders, winner)],
            enrollment.GetProperty("label").GetString());
        Assert.True(Enrollment.Verify(pairKey, invite, winner.Id, winner.Key, enrollment.GetProperty("mac").GetString()!));

        // A later attempt over the API is told the invitation is used, in its own code.
        var late = contenders.First(device => device != winner);
        using var again = await PostEnrollmentAsync(
            alice, invite, late.Id, Enrollment.Mac(pairKey, invite, late.Id, late.Key));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invite-used", (await ErrorAsync(again)).Code);
    }

    [Fact]
    public async Task An_expired_invite_is_refused()
    {
        var owner = await PersonAsync("alice");
        var start = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        var clock = new MovableClock(start);
        var devices = new DeviceService(Db, Limits.Unlimited, clock);
        var inviter = new DeviceAccess((await DeviceAsync(owner, "laptop")).Id, owner.UserId);
        var phone = await DeviceAsync(owner, "phone");
        var lapsed = Ids.New();
        var timely = Ids.New();

        Assert.Equal(start.AddMinutes(10), await devices.CreateInviteAsync(inviter, lapsed, default));
        Assert.Equal(start.AddMinutes(10), await devices.CreateInviteAsync(inviter, timely, default));

        clock.Now = start.AddMinutes(10).AddSeconds(-1);
        await devices.EnrollAsync(owner, timely, phone.Id, MacOf(timely, phone), default);

        clock.Now = start.AddMinutes(10);
        var refused = await Assert.ThrowsAsync<GatewayFault>(
            () => devices.EnrollAsync(owner, lapsed, phone.Id, MacOf(lapsed, phone), default));

        Assert.Equal("invite-expired", refused.Code);
        Assert.Equal(410, refused.Status);
        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM enrollments WHERE invite_id = '{lapsed}'"));
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM invites WHERE id = '{lapsed}' AND consumed_at IS NULL"));
    }

    [Fact]
    public async Task Bob_cannot_enroll_with_alices_invite()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        using var bob = await PanelClient.SignedInAsync(_gateway, Name("bob"));
        var owner = new UserAccess(alice.UserId, "unused");
        var laptop = await DeviceAsync(owner, "laptop");
        var phone = await DeviceAsync(owner, "phone");
        var bobs = await DeviceAsync(new UserAccess(bob.UserId, "unused"), "bobs");
        var invite = Ids.New();
        var missing = Ids.New();

        using (var created = await PostInviteAsync(alice, laptop.Id, invite))
        {
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        }

        using var foreign = await PostEnrollmentAsync(bob, invite, bobs.Id, MacOf(invite, bobs));
        using var absent = await PostEnrollmentAsync(bob, missing, bobs.Id, MacOf(missing, bobs));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await absent.Content.ReadAsStringAsync(), await foreign.Content.ReadAsStringAsync());
        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM enrollments WHERE invite_id = '{invite}'"));
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM invites WHERE id = '{invite}' AND consumed_at IS NULL"));

        // Nor can he answer it with Alice's device: that is not a device of his.
        using var hers = await PostEnrollmentAsync(bob, invite, phone.Id, MacOf(invite, phone));
        Assert.Equal(HttpStatusCode.NotFound, hers.StatusCode);

        // Untouched, so Alice's own new device still can.
        using var own = await PostEnrollmentAsync(alice, invite, phone.Id, MacOf(invite, phone));
        Assert.True(own.IsSuccessStatusCode, await own.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_computer_sees_enrollments_for_its_own_invites_only()
    {
        var owner = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var studio = await ComputerAsync(owner);
        var office = await ComputerAsync(owner);
        var bobsComputer = await ComputerAsync(bob);
        var laptop = await DeviceAsync(owner, "laptop");
        var phone = await DeviceAsync(owner, "phone");
        var tablet = await DeviceAsync(owner, "tablet");
        var desk = await DeviceAsync(owner, "desk");
        var bobs = await DeviceAsync(bob, "bobs");
        using var limit = new HostCallLimit();
        var studioHub = Hub(studio, limit);
        var (fromStudio, fromOffice, fromBrowser, fromBob) = (Ids.New(), Ids.New(), Ids.New(), Ids.New());

        Assert.Null((await studioHub.CreateInvite(fromStudio)).Fault);
        Assert.Null((await Hub(office, limit).CreateInvite(fromOffice)).Fault);
        Assert.Null((await Hub(bobsComputer, limit).CreateInvite(fromBob)).Fault);
        await Devices.CreateInviteAsync(new DeviceAccess(laptop.Id, owner.UserId), fromBrowser, default);

        await Devices.EnrollAsync(owner, fromStudio, phone.Id, MacOf(fromStudio, phone), default);
        await Devices.EnrollAsync(owner, fromOffice, tablet.Id, MacOf(fromOffice, tablet), default);
        await Devices.EnrollAsync(owner, fromBrowser, desk.Id, MacOf(fromBrowser, desk), default);
        await Devices.EnrollAsync(bob, fromBob, bobs.Id, MacOf(fromBob, bobs), default);

        var seen = (await studioHub.Enrollments()).Value!;
        Assert.Equal(
            new EnrollmentView(fromStudio, phone.Id, B64.Url(phone.Key), "phone", MacOf(fromStudio, phone)),
            Assert.Single(seen));

        // Answered, it is not handed over again; answering twice is a retry after a lost reply, not an error.
        Assert.Null((await studioHub.AnsweredInvite(fromStudio)).Fault);
        Assert.Empty((await studioHub.Enrollments()).Value!);
        Assert.Null((await studioHub.AnsweredInvite(fromStudio)).Fault);

        // Another computer's invitation and another person's are refused exactly like one that does not
        // exist, with a code the computer drops rather than retries.
        var absent = (await studioHub.AnsweredInvite(Ids.New())).Fault!;
        Assert.Equal(FaultCode.UnknownInvite, absent.Code);
        Assert.Equal(FaultDisposition.Drop, absent.Disposition);
        Assert.Equal(absent, (await studioHub.AnsweredInvite(fromOffice)).Fault);
        Assert.Equal(absent, (await studioHub.AnsweredInvite(fromBob)).Fault);
        Assert.Equal(absent, (await studioHub.AnsweredInvite(fromBrowser)).Fault);

        // The office computer's is still waiting for it, and Bob's computer sees only his own.
        Assert.Equal(fromOffice, Assert.Single((await Hub(office, limit).Enrollments()).Value!).InviteId);
        Assert.Equal(fromBob, Assert.Single((await Hub(bobsComputer, limit).Enrollments()).Value!).InviteId);
    }

    [Fact]
    public async Task The_sixth_open_invite_is_refused()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var start = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        var clock = new MovableClock(start);
        var devices = new DeviceService(Db, Limits.Unlimited with { OpenInvitesPerUser = 5 }, clock);
        var browser = new DeviceAccess((await DeviceAsync(alice, "laptop")).Id, alice.UserId);
        var computer = await ComputerAsync(alice);
        var phone = await DeviceAsync(alice, "phone");
        var first = Ids.New();

        await devices.CreateInviteAsync(browser, first, default);
        await devices.CreateInviteAsync(browser, Ids.New(), default);
        await devices.CreateInviteAsync(browser, Ids.New(), default);
        await devices.CreateInviteAsync(computer, Ids.New(), default);
        await devices.CreateInviteAsync(computer, Ids.New(), default);

        async Task AssertFullAsync()
        {
            foreach (var attempt in new Func<Task>[]
                     {
                         () => devices.CreateInviteAsync(browser, Ids.New(), default),
                         () => devices.CreateInviteAsync(computer, Ids.New(), default)
                     })
            {
                var refused = await Assert.ThrowsAsync<GatewayFault>(attempt);
                Assert.Equal("invite-limit", refused.Code);
                Assert.Equal(409, refused.Status);
                Assert.Contains("5 open invitations", refused.Message);
            }
        }

        await AssertFullAsync();

        // Another person has an allowance of their own.
        await devices.CreateInviteAsync(new DeviceAccess((await DeviceAsync(bob, "bobs")).Id, bob.UserId), Ids.New(), default);

        // A used invitation is no longer open, and frees its place.
        await devices.EnrollAsync(alice, first, phone.Id, MacOf(first, phone), default);
        await devices.CreateInviteAsync(browser, Ids.New(), default);
        await AssertFullAsync();

        // So does an expired one, though nothing has deleted it.
        clock.Now = start.AddMinutes(10);
        for (var i = 0; i < 5; i++)
        {
            await (i % 2 == 0
                ? devices.CreateInviteAsync(browser, Ids.New(), default)
                : devices.CreateInviteAsync(computer, Ids.New(), default));
        }

        await AssertFullAsync();
    }

    /// <summary>
    /// Invitations made at once, by browsers and computers of one person's, racing for the last places: the
    /// count is taken under the account's lock, so each waits for the one before it and then counts it.
    /// </summary>
    [Fact]
    public async Task Concurrent_invitations_cannot_pass_the_limit_together()
    {
        var alice = await PersonAsync("alice");
        var devices = new DeviceService(Db, Limits.Unlimited with { OpenInvitesPerUser = 3 }, TimeProvider.System);
        var browser = new DeviceAccess((await DeviceAsync(alice, "laptop")).Id, alice.UserId);
        var computer = await ComputerAsync(alice);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async n =>
        {
            try
            {
                await (n % 2 == 0
                    ? devices.CreateInviteAsync(browser, Ids.New(), default)
                    : devices.CreateInviteAsync(computer, Ids.New(), default));
                return true;
            }
            catch (GatewayFault fault) when (fault.Code == "invite-limit")
            {
                return false;
            }
        }));

        Assert.Equal(3, attempts.Count(ok => ok));
        Assert.Equal(3, await database.ScalarLongAsync($"SELECT COUNT(*) FROM invites WHERE owner_id = '{alice.UserId}'"));
    }

    /// <summary>
    /// An invitation's id is the caller's to make, and it names one invitation. Used again - by the same
    /// person or anyone else - it is refused rather than made to name a second invitation.
    /// </summary>
    [Fact]
    public async Task An_invite_id_names_one_invitation()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var laptop = new DeviceAccess((await DeviceAsync(alice, "laptop")).Id, alice.UserId);
        var computer = await ComputerAsync(alice);
        var bobs = new DeviceAccess((await DeviceAsync(bob, "bobs")).Id, bob.UserId);
        var id = Ids.New();
        await Devices.CreateInviteAsync(laptop, id, default);

        foreach (var attempt in new Func<Task>[]
                 {
                     () => Devices.CreateInviteAsync(laptop, id, default),
                     () => Devices.CreateInviteAsync(computer, id, default),
                     () => Devices.CreateInviteAsync(bobs, id, default)
                 })
        {
            var refused = await Assert.ThrowsAsync<GatewayFault>(attempt);
            Assert.Equal("conflict", refused.Code);
            Assert.Equal(409, refused.Status);
        }

        Assert.Equal(1, await database.ScalarLongAsync($"SELECT COUNT(*) FROM invites WHERE id = '{id}'"));
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM audit WHERE owner_id = '{alice.UserId}' AND actor = 'user:{alice.UserId}' "
            + $"AND action = 'invite-created' AND target = '{id}'"));
    }

    /// <summary>
    /// Ids and the MAC as they are made and nothing else. The id columns ignore trailing spaces, so "id "
    /// would find the invitation and store an enrollment under a spelling nobody asks for; a MAC that is
    /// not 32 bytes of base64url could never verify, and the inviter would only find out by refusing it.
    /// </summary>
    [Theory]
    [InlineData("inviteId", "trailing-space")]
    [InlineData("inviteId", "uppercase")]
    [InlineData("deviceId", "trailing-space")]
    [InlineData("mac", "short")]
    [InlineData("mac", "padded")]
    [InlineData("mac", "missing")]
    public async Task A_malformed_enrollment_is_refused(string field, string variant)
    {
        var owner = await PersonAsync("alice");
        var laptop = new DeviceAccess((await DeviceAsync(owner, "laptop")).Id, owner.UserId);
        var phone = await DeviceAsync(owner, "phone");
        var invite = Ids.New();
        await Devices.CreateInviteAsync(laptop, invite, default);
        var mac = MacOf(invite, phone);

        var refused = await Assert.ThrowsAsync<GatewayFault>(() => (field, variant) switch
        {
            ("inviteId", "trailing-space") => Devices.EnrollAsync(owner, invite + " ", phone.Id, mac, default),
            ("inviteId", _) => Devices.EnrollAsync(owner, invite.ToUpperInvariant(), phone.Id, mac, default),
            ("deviceId", _) => Devices.EnrollAsync(owner, invite, phone.Id + " ", mac, default),
            ("mac", "short") => Devices.EnrollAsync(owner, invite, phone.Id, B64.Url(new byte[31]), default),
            ("mac", "padded") => Devices.EnrollAsync(owner, invite, phone.Id, mac + "=", default),
            _ => Devices.EnrollAsync(owner, invite, phone.Id, null, default)
        });

        Assert.Equal("bad-request", refused.Code);
        Assert.Contains($"'{field}'", refused.Message);
        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM enrollments WHERE invite_id = '{invite}'"));
    }

    [Fact]
    public async Task Inviting_and_enrolling_need_a_live_device_of_the_callers()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var owner = new UserAccess(alice.UserId, "unused");
        var laptop = await DeviceAsync(owner, "laptop");
        var phone = await DeviceAsync(owner, "phone");
        var invite = Ids.New();

        using (var unnamed = await alice.SendAsync(HttpMethod.Post, "/api/invites", new { id = invite }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
            Assert.Equal("device-header", (await ErrorAsync(unnamed)).Code);
        }

        using (var created = await PostInviteAsync(alice, laptop.Id, invite))
        {
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        }

        await Devices.RevokeAsync(owner, phone.Id, default);
        using var removed = await PostEnrollmentAsync(alice, invite, phone.Id, MacOf(invite, phone));

        Assert.Equal(HttpStatusCode.Forbidden, removed.StatusCode);
        Assert.Equal("device-revoked", (await ErrorAsync(removed)).Code);
        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM enrollments WHERE invite_id = '{invite}'"));

        // A removed browser can no longer make invitations either.
        await Devices.RevokeAsync(owner, laptop.Id, default);
        using var revoked = await PostInviteAsync(alice, laptop.Id, Ids.New());
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>An enrollment MAC, made the way a new device makes it, under a pair key nobody else holds.</summary>
    private static string MacOf(string inviteId, (string Id, byte[] Key) device)
        => Enrollment.Mac(PairKeyOfTests, inviteId, device.Id, device.Key);

    // One key for the MACs these tests make, so a test can make the same MAC twice and compare it with the
    // stored one. The gateway never checks it: it has no pair key.
    private static readonly byte[] PairKeyOfTests = RandomNumberGenerator.GetBytes(32);

    private static Task<HttpResponseMessage> PostInviteAsync(PanelClient browser, string deviceId, string inviteId)
        => browser.SendAsync(HttpMethod.Post, "/api/invites", new { id = inviteId },
            configure: request => request.Headers.Add(DeviceHeader.Name, deviceId));

    private static Task<HttpResponseMessage> PostEnrollmentAsync(
        PanelClient browser, string inviteId, string deviceId, string mac)
        => browser.SendAsync(HttpMethod.Post, "/api/enrollments", new { inviteId, deviceId, mac });

    private static Task<HttpResponseMessage> ReadEnrollmentAsync(PanelClient browser, string deviceId, string inviteId)
        => browser.SendAsync(HttpMethod.Get, $"/api/invites/{inviteId}/enrollment",
            configure: request => request.Headers.Add(DeviceHeader.Name, deviceId));

    private UserService Users => new(Db, Limits.Unlimited, TimeProvider.System);

    private async Task<HostAccess> ComputerAsync(UserAccess owner)
    {
        var (id, _, _) = await Users.RegisterHostAsync(owner, "Studio PC", default);
        return new HostAccess(id, owner.UserId);
    }

    /// <summary>A device of <paramref name="owner"/>'s, with the public key grants to it are sealed to.</summary>
    private async Task<(string Id, byte[] Key)> DeviceAsync(UserAccess owner, string label = "laptop")
    {
        var key = NewPublicKey();
        return (await Devices.RegisterAsync(owner, key, label, default), key);
    }

    /// <summary>
    /// A grant authenticated with a pair key: <c>connect</c> for the one answering a computer's pairing,
    /// 32 hex characters for one answering an invitation.
    /// </summary>
    private static KeyGrant Paired(
        string hostId, (string Id, byte[] Key) device, uint epoch, ECDsa signer, string pairing = "connect")
        => Grants.CreatePaired(hostId, device.Id, device.Key, HostKey.Create(epoch), pairing,
            RandomNumberGenerator.GetBytes(32), P256.SigningPublicRaw(signer));

    /// <summary>A rotation grant, signed by the computer.</summary>
    private static KeyGrant Signed(string hostId, (string Id, byte[] Key) device, uint epoch, ECDsa signer)
        => Grants.CreateSigned(hostId, device.Id, device.Key, HostKey.Create(epoch), signer);

    private static KeyGrant Malformed(KeyGrant grant, string field, string variant) => (field, variant) switch
    {
        ("epoch", _) => grant with { Epoch = 0 },
        ("ephemeralPublic", "short") => grant with { EphemeralPublic = B64.Url(new byte[64]) },
        ("ephemeralPublic", _) => grant with { EphemeralPublic = B64.Url(OffCurveKey()) },
        ("nonce", _) => grant with { Nonce = B64.Url(new byte[11]) },
        ("ciphertext", _) => grant with { Ciphertext = B64.Url(new byte[47]) },
        ("mac", "signature-length") => grant with { Mac = B64.Url(new byte[64]) },
        ("mac", _) => grant with { Mac = "not base64url!" },
        ("authBy", "uppercase") => grant with { AuthBy = "pair:" + Ids.New().ToUpperInvariant() },
        ("authBy", "trailing-newline") => grant with { AuthBy = "pair:connect\n" },
        ("authBy", _) => grant with { AuthBy = "device" },
        ("hostSigningPublic", "short") => grant with { HostSigningPublic = B64.Url(new byte[64]) },
        ("hostSigningPublic", "off-curve") => grant with { HostSigningPublic = B64.Url(OffCurveKey()) },

        // The same key, spelled with the padding base64url leaves out: the bytes match the pin, the text a
        // device compares does not.
        ("hostSigningPublic", _) => grant with { HostSigningPublic = grant.HostSigningPublic + "=" },
        ("hostId", _) => grant with { HostId = null! },
        ("deviceId", _) => grant with { DeviceId = null! },
        _ => throw new ArgumentException($"No such malformation: {field}/{variant}.")
    };

    private HostHub Hub(HostAccess host, HostCallLimit limit)
        => new(new HostService(Db), Devices, new HostConnections(), limit) { Context = new ComputerCaller(host) };

    private Task<long> GrantCountAsync(string hostId)
        => database.ScalarLongAsync($"SELECT COUNT(*) FROM grants WHERE host_id = '{hostId}'");

    private Task<long> KeyEpochAsync(string hostId)
        => database.ScalarLongAsync($"SELECT key_epoch FROM hosts WHERE id = '{hostId}'");

    private async Task<byte[]?> SigningPublicAsync(string hostId)
        => await database.ScalarAsync($"SELECT signing_public FROM hosts WHERE id = '{hostId}'") as byte[];

    private static Task<HttpResponseMessage> ReadGrantsAsync(PanelClient browser, string? deviceId)
        => browser.SendAsync(HttpMethod.Get, "/api/grants", configure: request =>
        {
            if (deviceId is not null)
            {
                request.Headers.Add(DeviceHeader.Name, deviceId);
            }
        });

    private static async Task<List<HostGrantsView>> GrantsOfAsync(PanelClient browser, string deviceId)
    {
        using var response = await ReadGrantsAsync(browser, deviceId);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<List<HostGrantsView>>(RemoteJson.Options))!;
    }

    private static Task<HttpResponseMessage> PostGrantsAsync(PanelClient browser, string deviceId, params KeyGrant[] grants)
        => browser.SendAsync(HttpMethod.Post, "/api/grants", grants,
            configure: request => request.Headers.Add(DeviceHeader.Name, deviceId));

    /// <summary>What <c>GET /api/grants</c> answers for one computer. Every field, because the wire refuses unknown ones.</summary>
    private sealed record HostGrantsView(string HostId, uint KeyEpoch, List<KeyGrant> Grants);

    /// <summary>A clock a test moves by hand.</summary>
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The connection a hub method is called on, as a computer's token opens it: which computer, and whose.</summary>
    private sealed class ComputerCaller(HostAccess host) : HubCallerContext
    {
        public override string ConnectionId { get; } = Guid.NewGuid().ToString();

        public override string? UserIdentifier { get; } = host.HostId;

        public override ClaimsPrincipal? User { get; } = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, host.HostId), new Claim(HostAuthentication.OwnerClaim, host.OwnerId)],
            HostAuthentication.SchemeName));

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }

    private static async Task AssertRefusedAsync(PanelClient browser, string publicKey, string code)
    {
        using var response = await browser.SendAsync(
            HttpMethod.Post, "/api/devices", new { publicKey, label = "laptop" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await ErrorAsync(response)).Code);
    }

    private static async Task<ErrorBody> ErrorAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<ErrorBody>(RemoteJson.Options))!;

    private sealed record ErrorBody(string Code, string Error);
}
