namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

/// <summary>
/// The security log: one row for each thing done to a person's access - a sign-in, a device or a computer
/// added or removed, the operator stopping or reopening the account, a sign-out everywhere - written in the
/// transaction that did it, and readable by that person alone.
///
/// <para>Each action is checked for writing exactly one row, with the actor and the target it names: a log
/// that wrote two rows for one removal, or none for a sign-in, is a log the person cannot read their account's
/// history from. Signing in through a provider is checked end to end in <see cref="SignInTests"/>, where the
/// fake providers are.</para>
/// </summary>
public sealed class AuditTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
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

    // ── one row per action ──────────────────────────────────────────────────

    [Fact]
    public async Task Signing_in_by_name_writes_one_row()
    {
        using var browser = new PanelClient(_gateway);
        await browser.SessionAsync();
        var before = await NewestIdAsync();

        await browser.SignInAsync(Name("alice"));

        Assert.Equal(
            [new Row(browser.UserId, "user:" + browser.UserId, "signin.dev", null)],
            await RowsAfterAsync(before));
    }

    [Fact]
    public async Task Registering_a_device_writes_one_row()
    {
        var alice = await PersonAsync("alice");
        var before = await NewestIdAsync();

        var id = await Devices.RegisterAsync(alice, NewPublicKey(), "laptop", default);

        Assert.Equal([Mine(alice, Audit.DeviceRegistered, id)], await RowsAfterAsync(before));
    }

    [Fact]
    public async Task Inviting_a_device_writes_one_row()
    {
        var alice = await PersonAsync("alice");
        var laptop = await Devices.RequireAsync(
            alice, await Devices.RegisterAsync(alice, NewPublicKey(), "laptop", default), default);
        var invite = Ids.New();
        var before = await NewestIdAsync();

        await Devices.CreateInviteAsync(laptop, invite, default);

        Assert.Equal([Mine(alice, Audit.InviteCreated, invite)], await RowsAfterAsync(before));
    }

    [Fact]
    public async Task Enrolling_a_device_writes_one_row()
    {
        var alice = await PersonAsync("alice");
        var laptop = await Devices.RequireAsync(
            alice, await Devices.RegisterAsync(alice, NewPublicKey(), "laptop", default), default);
        var phoneKey = NewPublicKey();
        var phone = await Devices.RegisterAsync(alice, phoneKey, "phone", default);
        var invite = Ids.New();
        await Devices.CreateInviteAsync(laptop, invite, default);
        var before = await NewestIdAsync();

        await Devices.EnrollAsync(
            alice, invite, phone, Enrollment.Mac(RandomNumberGenerator.GetBytes(32), invite, phone, phoneKey), default);

        Assert.Equal([Mine(alice, Audit.DeviceEnrolled, phone)], await RowsAfterAsync(before));
    }

    [Fact]
    public async Task Removing_a_device_writes_one_row()
    {
        var alice = await PersonAsync("alice");
        var id = await Devices.RegisterAsync(alice, NewPublicKey(), "laptop", default);
        var before = await NewestIdAsync();

        await Devices.RevokeAsync(alice, id, default);

        Assert.Equal([Mine(alice, Audit.DeviceRevoked, id)], await RowsAfterAsync(before));
    }

    [Fact]
    public async Task Registering_a_computer_writes_one_row()
    {
        var alice = await PersonAsync("alice");
        var before = await NewestIdAsync();

        var (id, _, _) = await Users.RegisterHostAsync(alice, "Studio PC", default);

        Assert.Equal([Mine(alice, Audit.HostRegistered, id)], await RowsAfterAsync(before));
    }

    [Fact]
    public async Task Removing_a_computer_writes_one_row()
    {
        var alice = await PersonAsync("alice");
        var (id, _, _) = await Users.RegisterHostAsync(alice, "Studio PC", default);
        var before = await NewestIdAsync();

        await Users.RevokeHostAsync(alice, id, default);

        Assert.Equal([Mine(alice, Audit.HostRevoked, id)], await RowsAfterAsync(before));
    }

    /// <summary>
    /// Revoking a computer that is already revoked succeeds and changes nothing, so it writes nothing: a
    /// second row would tell the person their computer was removed twice.
    /// </summary>
    [Fact]
    public async Task Removing_a_computer_twice_writes_one_row()
    {
        var alice = await PersonAsync("alice");
        var (id, _, _) = await Users.RegisterHostAsync(alice, "Studio PC", default);
        var before = await NewestIdAsync();

        await Users.RevokeHostAsync(alice, id, default);
        await Users.RevokeHostAsync(alice, id, default);

        Assert.Equal([Mine(alice, Audit.HostRevoked, id)], await RowsAfterAsync(before));
    }

    [Theory]
    [InlineData("disable", "account.disabled")]
    [InlineData("enable", "account.enabled")]
    public async Task The_operator_disabling_or_enabling_an_account_writes_one_row(string command, string action)
    {
        var alice = await PersonAsync("alice");
        var before = await NewestIdAsync();

        Assert.Equal(0, await AdminCommands.RunAsync([command, alice.UserId], Db, TextWriter.Null));

        Assert.Equal([new Row(alice.UserId, Audit.Operator, action, alice.UserId)], await RowsAfterAsync(before));
    }

    [Fact]
    public async Task The_operator_signing_an_account_out_everywhere_writes_one_row()
    {
        var alice = await PersonAsync("alice");
        var before = await NewestIdAsync();

        Assert.Equal(0, await AdminCommands.RunAsync(["sessions", "revoke", alice.UserId], Db, TextWriter.Null));

        Assert.Equal(
            [new Row(alice.UserId, Audit.Operator, Audit.SessionsRevoked, alice.UserId)], await RowsAfterAsync(before));
    }

    [Fact]
    public async Task Signing_out_everywhere_writes_one_row()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var before = await NewestIdAsync();

        using var response = await browser.SendAsync(HttpMethod.Post, "/api/logout-all");
        response.EnsureSuccessStatusCode();

        Assert.Equal(
            [new Row(browser.UserId, "user:" + browser.UserId, Audit.SignOutEverywhere, null)],
            await RowsAfterAsync(before));
    }

    /// <summary>
    /// Signing out of this browser only is not an event of the log's: the session's own row records it,
    /// and a row for every sign-out would bury the ones that matter.
    /// </summary>
    [Fact]
    public async Task Signing_out_of_one_browser_writes_nothing()
    {
        using var browser = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        var before = await NewestIdAsync();

        using var response = await browser.SendAsync(HttpMethod.Post, "/api/logout");
        response.EnsureSuccessStatusCode();

        Assert.Empty(await RowsAfterAsync(before));
    }

    // ── reading it ──────────────────────────────────────────────────────────

    /// <summary>
    /// Alice reads her rows and no row of Bob's, though both did the same things; the operator's admission
    /// of an identity with no account yet belongs to nobody and is read by nobody. Each row is exactly
    /// <c>at</c>, <c>actor</c>, <c>action</c> and <c>target</c>: the wire refuses a member it does not know.
    /// </summary>
    [Fact]
    public async Task A_user_sees_only_their_own_audit()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        using var bob = await PanelClient.SignedInAsync(_gateway, Name("bob"));
        var hers = await RegisterDeviceAsync(alice);
        var his = await RegisterDeviceAsync(bob);
        Assert.Equal(0, await AdminCommands.RunAsync(["approve", $"github:{Ids.New()}"], Db, TextWriter.Null));

        var aliceSees = await alice.GetAsync<List<Entry>>("/api/audit");
        var bobSees = await bob.GetAsync<List<Entry>>("/api/audit");

        Assert.Equal(
            [("user:" + alice.UserId, Audit.DeviceRegistered, hers), ("user:" + alice.UserId, "signin.dev", null)],
            aliceSees.Select(row => (row.Actor, row.Action, row.Target)));
        Assert.Equal(
            [("user:" + bob.UserId, Audit.DeviceRegistered, his), ("user:" + bob.UserId, "signin.dev", null)],
            bobSees.Select(row => (row.Actor, row.Action, row.Target)));

        var aliceText = JsonSerializer.Serialize(aliceSees);
        Assert.DoesNotContain(bob.UserId, aliceText, StringComparison.Ordinal);
        Assert.DoesNotContain(his, aliceText, StringComparison.Ordinal);
        Assert.DoesNotContain("admission", aliceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_log_needs_a_session()
    {
        using var anonymous = _gateway.CreateClient();

        using var response = await anonymous.GetAsync("/api/audit");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The newest first, and no more than the cap: the trail is kept for months, and a busy account's whole
    /// of it in one answer would be a reply that grows without bound on every open of the dialog.
    /// </summary>
    [Fact]
    public async Task The_log_is_the_newest_rows_first_and_capped()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        await alice.EnsureDeviceAsync();
        var start = DateTimeOffset.UtcNow.AddDays(-1);

        for (var i = 0; i < Audit.Newest + 5; i++)
        {
            await database.ExecuteAsync(
                """
                INSERT INTO audit (owner_id, at, actor, action, target)
                VALUES (@owner, @at, @actor, 'device.registered', @target)
                """,
                ("@owner", alice.UserId), ("@at", start.AddSeconds(i)), ("@actor", "user:" + alice.UserId),
                ("@target", i.ToString("D32")));
        }

        var rows = await alice.GetAsync<List<Entry>>("/api/audit");

        // The sign-in and the browser's registering its device are the newest, then the rows written above.
        Assert.Equal(Audit.Newest, rows.Count);
        Assert.Equal(["device.registered", "signin.dev"], rows.Take(2).Select(row => row.Action));
        Assert.Equal((Audit.Newest + 4).ToString("D32"), rows[2].Target);
        Assert.Equal(rows.OrderByDescending(row => row.At).Select(row => row.At), rows.Select(row => row.At));
    }

    // ── what a row holds ────────────────────────────────────────────────────

    /// <summary>
    /// A row is who, what and to which id - never an address, a key, a label, a provider's subject or an
    /// envelope. The table has no column to put one in, and what the actions here wrote is ids and names of
    /// actions only.
    /// </summary>
    [Fact]
    public async Task Audit_rows_carry_no_address_and_no_envelope()
    {
        Assert.Equal(
            ["action", "actor", "at", "id", "owner_id", "target"],
            await database.StringsAsync(
                $"""
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = '{database.Name}' AND table_name = 'audit' ORDER BY column_name
                """));

        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        // The browser's own device first: removing the device a session is bound to ends the session, and the
        // tablet removed below is another one.
        await alice.EnsureDeviceAsync();
        var key = B64.Url(NewPublicKey());
        var device = await alice.PostAsync<JsonElement>(
            "/api/devices", new { publicKey = key, label = "Kitchen tablet" });
        var id = device.GetProperty("id").GetString()!;
        await alice.PostAsync($"/api/devices/{id}/revoke", new { });
        await alice.PostAsync("/api/logout-all", new { });

        var rows = await database.StringsAsync(
            $"SELECT CONCAT_WS('|', actor, action, IFNULL(target, '-')) FROM audit WHERE owner_id = '{alice.UserId}'");

        Assert.Equal(5, rows.Count);

        foreach (var row in rows)
        {
            Assert.Matches(new Regex(@"^(user|host):[0-9a-f]{32}\|[a-z]+\.[a-z]+\|([0-9a-f]{32}|-)\z"), row);
            Assert.DoesNotContain(key, row, StringComparison.Ordinal);
            Assert.DoesNotContain("Kitchen", row, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", row, StringComparison.Ordinal);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>A row as a test compares it: everything but its id and its time.</summary>
    private sealed record Row(string? Owner, string Actor, string Action, string? Target);

    /// <summary>A row as <c>GET /api/audit</c> answers it.</summary>
    private sealed record Entry(DateTimeOffset At, string Actor, string Action, string? Target);

    private static Row Mine(UserAccess user, string action, string target)
        => new(user.UserId, "user:" + user.UserId, action, target);

    private UserService Users => new(Db, Limits.Unlimited, TimeProvider.System);

    private static string Name(string stem) => stem + "-" + Guid.NewGuid().ToString("N")[..8];

    private Task<UserAccess> PersonAsync(string stem) => TestAccounts.CreateAsync(database, Name(stem));

    private static byte[] NewPublicKey()
    {
        using var key = P256.Generate();
        return P256.PublicRaw(key);
    }

    private static async Task<string> RegisterDeviceAsync(PanelClient browser)
        => (await browser.PostAsync<JsonElement>(
                "/api/devices", new { publicKey = B64.Url(NewPublicKey()), label = "laptop" }))
            .GetProperty("id").GetString()!;

    /// <summary>The newest row's id, so a test reads only what its own action wrote.</summary>
    private async Task<long> NewestIdAsync()
        => await database.ScalarLongAsync("SELECT IFNULL(MAX(id), 0) FROM audit");

    private async Task<List<Row>> RowsAfterAsync(long id)
    {
        await using var connection = await database.OpenAsync();
        return await connection.ReadAllAsync(null,
            "SELECT owner_id, actor, action, target FROM audit WHERE id > @id ORDER BY id",
            reader => new Row(
                reader.StringOrNull("owner_id"), reader.GetString("actor"), reader.GetString("action"),
                reader.StringOrNull("target")),
            ("@id", id));
    }
}
