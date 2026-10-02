namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
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

    // ── helpers ─────────────────────────────────────────────────────────────

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
