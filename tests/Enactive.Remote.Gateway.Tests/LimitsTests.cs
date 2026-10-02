namespace Enactive.Remote.Gateway.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// What one account may hold or start, checked where the thing is created and under the account's lock,
/// so requests made at once cannot all take the last place; and the rows an account cannot grow without
/// bound - removed devices, the audit trail, sealed history - kept within bounds.
/// </summary>
public sealed class LimitsTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private Database Db => new(database.ConnectionString);

    private static string Uuid() => Guid.NewGuid().ToString();

    private static string Name(string stem) => stem + Guid.NewGuid().ToString("N")[..8];

    private Task<UserAccess> PersonAsync(string stem) => TestAccounts.CreateAsync(database, Name(stem));

    private static string Sealed(string text)
        => Envelope.Seal(RandomNumberGenerator.GetBytes(32), 1, Encoding.UTF8.GetBytes(text), []);

    private static byte[] NewPublicKey()
    {
        using var key = P256.Generate();
        return P256.PublicRaw(key);
    }

    /// <summary>The real gateway with <paramref name="limits"/> in place of the configured ones.</summary>
    private WebApplicationFactory<Program> Gateway(Limits limits)
        => TestGateway.Create(database, configure: builder =>
            builder.ConfigureTestServices(services => services.AddSingleton(limits)));

    /// <summary>A computer of the person's that has published one workspace, so tasks can be made on it.</summary>
    private static async Task<HostAccess> ComputerAsync(UserService users, HostService hosts, UserAccess user)
    {
        var (id, _, _) = await users.RegisterHostAsync(user, "Studio PC", default);
        var host = new HostAccess(id, user.UserId);
        await hosts.SyncAsync(host, [new WorkspaceRef("workspace-1", Sealed("Enactive"))]);
        return host;
    }

    private Task<long> CountAsync(string sql) => database.ScalarLongAsync(sql);

    private Task<long> SealedBytesAsync(UserAccess user)
        => CountAsync($"SELECT sealed_bytes FROM users WHERE id = '{user.UserId}'");

    // ── computers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Ten registrations at once against a limit of three: exactly three computers, and seven refusals
    /// that say it is the limit. Shown red by counting without the account's lock first - each request then
    /// reads "none yet" in its own snapshot and inserts.
    /// </summary>
    [Fact]
    public async Task Ten_concurrent_host_registrations_stop_at_the_limit()
    {
        await using var gateway = Gateway(Limits.Defaults with { HostsPerUser = 3 });
        using var browser = await PanelClient.SignedInAsync(gateway, Name("alice"));

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(n =>
            browser.SendAsync(HttpMethod.Post, "/api/hosts", new { name = $"PC {n}" })));

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        foreach (var refused in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            Assert.Equal("quota-exceeded", (await refused.Content.ReadFromJsonAsync<Refusal>())!.Code);
        }

        Assert.Equal(3, await CountAsync($"SELECT COUNT(*) FROM hosts WHERE owner_id = '{browser.UserId}'"));
    }

    /// <summary>
    /// The refusal says what the limit is, in the sentence the panel shows; and a removed computer no longer
    /// takes a place, since removing one is how the person makes room.
    /// </summary>
    [Fact]
    public async Task A_refused_quota_names_the_limit()
    {
        await using var gateway = Gateway(Limits.Defaults with { HostsPerUser = 2 });
        using var browser = await PanelClient.SignedInAsync(gateway, Name("alice"));

        var first = await browser.PostAsync<Registered>("/api/hosts", new { name = "One" });
        await browser.PostAsync<Registered>("/api/hosts", new { name = "Two" });

        using var response = await browser.SendAsync(HttpMethod.Post, "/api/hosts", new { name = "Three" });
        var refusal = (await response.Content.ReadFromJsonAsync<Refusal>())!;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("quota-exceeded", refusal.Code);
        Assert.Contains("2 computers", refusal.Error, StringComparison.Ordinal);

        await browser.PostAsync($"/api/hosts/{first.Id}/revoke", new { });
        await browser.PostAsync<Registered>("/api/hosts", new { name = "Three" });
    }

    // ── runs, tasks, commands ───────────────────────────────────────────────

    /// <summary>
    /// Runs that have not ended count against the account; one that ends gives its place back. Shown red by
    /// not counting: the third start is queued.
    /// </summary>
    [Fact]
    public async Task Active_runs_stop_at_the_limit()
    {
        var alice = await PersonAsync("alice");
        var limits = Limits.Unlimited with { ActiveRunsPerUser = 2 };
        var users = new UserService(Db, limits, TimeProvider.System);
        var host = await ComputerAsync(users, new HostService(Db, limits), alice);

        var tasks = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var taskId = Uuid();
            await users.CreateTaskAsync(alice, taskId, host.HostId, "workspace-1", Sealed($"task {i}"), default);
            tasks.Add(taskId);
        }

        var started = await users.StartAsync(alice, tasks[0], Uuid(), Sealed("start"), default);
        await users.StartAsync(alice, tasks[1], Uuid(), Sealed("start"), default);

        var refused = await Assert.ThrowsAsync<GatewayFault>(
            () => users.StartAsync(alice, tasks[2], Uuid(), Sealed("start"), default));
        Assert.Equal(FaultCode.QuotaExceeded, refused.Code);
        Assert.Equal(409, refused.Status);
        Assert.Contains("2 ", refused.Message, StringComparison.Ordinal);

        var runId = RemoteJson.Deserialize<StartTaskPayload>(started.Payload).RunId;
        await database.ExecuteAsync($"UPDATE runs SET status = 'Completed' WHERE id = '{runId}'");

        await users.StartAsync(alice, tasks[2], Uuid(), Sealed("start"), default);
    }

    /// <summary>
    /// Tasks made in the last day count; a day later the same account may make more, and a retried create
    /// of a task it already has is still answered at the limit. Shown red by not counting.
    /// </summary>
    [Fact]
    public async Task Tasks_per_day_stop_at_the_limit()
    {
        var alice = await PersonAsync("alice");
        var limits = Limits.Unlimited with { TasksPerDay = 2 };
        var users = new UserService(Db, limits, TimeProvider.System);
        var host = await ComputerAsync(users, new HostService(Db, limits), alice);

        var first = Uuid();
        var sealedFirst = Sealed("first");
        await users.CreateTaskAsync(alice, first, host.HostId, "workspace-1", sealedFirst, default);
        await users.CreateTaskAsync(alice, Uuid(), host.HostId, "workspace-1", Sealed("second"), default);

        var refused = await Assert.ThrowsAsync<GatewayFault>(() =>
            users.CreateTaskAsync(alice, Uuid(), host.HostId, "workspace-1", Sealed("third"), default));
        Assert.Equal(FaultCode.QuotaExceeded, refused.Code);

        // The retry of one it already has takes no place.
        await users.CreateTaskAsync(alice, first, host.HostId, "workspace-1", sealedFirst, default);

        var tomorrow = new UserService(Db, limits, new FixedClock(DateTimeOffset.UtcNow.AddHours(25)));
        await tomorrow.CreateTaskAsync(alice, Uuid(), host.HostId, "workspace-1", Sealed("third"), default);
    }

    /// <summary>
    /// Commands a computer has not collected count against that computer; another computer of the same
    /// person has its own allowance, and one the computer acknowledges frees a place. Shown red by not
    /// counting: a computer that is off for a day collects a queue as long as the person's script made it.
    /// </summary>
    [Fact]
    public async Task Queued_commands_per_computer_stop_at_the_limit()
    {
        var alice = await PersonAsync("alice");
        var limits = Limits.Unlimited with { QueuedCommandsPerHost = 2 };
        var users = new UserService(Db, limits, TimeProvider.System);
        var hosts = new HostService(Db, limits);
        var busy = await ComputerAsync(users, hosts, alice);
        var other = await ComputerAsync(users, hosts, alice);

        Task<HostCommand> RevokeOn(HostAccess host, string commandId, string seal)
            => users.SendDeviceCommandAsync(alice, host.HostId, CommandKind.RevokeDevice, commandId, seal, default);

        var firstId = Uuid();
        var firstSeal = Sealed("device-1");
        await RevokeOn(busy, firstId, firstSeal);
        await RevokeOn(busy, Uuid(), Sealed("device-2"));

        var refused = await Assert.ThrowsAsync<GatewayFault>(() => RevokeOn(busy, Uuid(), Sealed("device-3")));
        Assert.Equal(FaultCode.QuotaExceeded, refused.Code);

        // A retry of a command already queued is answered with it, and takes no place.
        Assert.Equal(firstId, (await RevokeOn(busy, firstId, firstSeal)).Id);

        await RevokeOn(other, Uuid(), Sealed("device-3"));

        await hosts.AcknowledgeAsync(busy, firstId);
        await RevokeOn(busy, Uuid(), Sealed("device-3"));
    }

    // ── sealed bytes ────────────────────────────────────────────────────────

    /// <summary>
    /// Every sealed field stored for the account is added to its total in the same transaction, and a write
    /// that would pass the limit is refused whole - from the person or from the computer. Shown red by not
    /// adding: the total stays at zero and nothing is refused.
    /// </summary>
    [Fact]
    public async Task Sealed_bytes_stop_at_the_limit()
    {
        var alice = await PersonAsync("alice");
        var sealedTask = Sealed("Run the tests");
        var limits = Limits.Unlimited with { SealedBytesPerUser = sealedTask.Length + 10 };
        var users = new UserService(Db, limits, TimeProvider.System);
        var hosts = new HostService(Db, limits);
        var host = await ComputerAsync(users, hosts, alice);

        var taskId = Uuid();
        await users.CreateTaskAsync(alice, taskId, host.HostId, "workspace-1", sealedTask, default);
        Assert.Equal(sealedTask.Length, await SealedBytesAsync(alice));

        var refused = await Assert.ThrowsAsync<GatewayFault>(() =>
            users.CreateTaskAsync(alice, Uuid(), host.HostId, "workspace-1", Sealed("Another"), default));
        Assert.Equal(FaultCode.QuotaExceeded, refused.Code);

        var start = await users.StartAsync(alice, taskId, Uuid(), Sealed("start"), default);
        var runId = RemoteJson.Deserialize<StartTaskPayload>(start.Payload).RunId;
        await hosts.PublishAsync(host, new HostEvent(Uuid(), runId, 1, RemoteEventKind.Running));

        var fromComputer = await Assert.ThrowsAsync<GatewayFault>(() => hosts.PublishAsync(host,
            new HostEvent(Uuid(), runId, 2, RemoteEventKind.Progress, Sealed("Built the solution"))));
        Assert.Equal(FaultCode.QuotaExceeded, fromComputer.Code);

        // Refused whole: no event, no step of the run, no bytes.
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM events WHERE run_id = '{runId}'"));
        Assert.Equal(1, await CountAsync($"SELECT applied_sequence FROM runs WHERE id = '{runId}'"));
        Assert.Equal(sealedTask.Length, await SealedBytesAsync(alice));
    }

    /// <summary>
    /// What retention deletes is taken off the account's total, by the size of exactly the rows it deleted.
    /// Shown red by trimming without subtracting: the account fills up once and stays full.
    /// </summary>
    [Fact]
    public async Task Retention_gives_the_bytes_back()
    {
        var alice = await PersonAsync("alice");
        var users = new UserService(Db, Limits.Unlimited, TimeProvider.System);
        var hosts = new HostService(Db, Limits.Unlimited);
        var host = await ComputerAsync(users, hosts, alice);

        var taskId = Uuid();
        await users.CreateTaskAsync(alice, taskId, host.HostId, "workspace-1", Sealed("Run the tests"), default);
        var start = await users.StartAsync(alice, taskId, Uuid(), Sealed("start"), default);
        var runId = RemoteJson.Deserialize<StartTaskPayload>(start.Payload).RunId;

        await hosts.PublishAsync(host, new HostEvent(Uuid(), runId, 1, RemoteEventKind.Running));
        await hosts.PublishAsync(host, new HostEvent(Uuid(), runId, 2, RemoteEventKind.Progress, Sealed("Built")));
        await hosts.PublishAsync(host, new HostEvent(Uuid(), runId, 3, RemoteEventKind.ApprovalRequested,
            Sealed("May I?"), new ApprovalRequest("approval-1", "call-1", "hash-1", true, Sealed("dotnet test"))));
        await hosts.PublishAsync(host, new HostEvent(Uuid(), runId, 4, RemoteEventKind.Completed, Sealed("Done")));

        var owner = $"owner_id = '{alice.UserId}'";
        var stored = await CountAsync(
            $"""
            SELECT (SELECT COALESCE(SUM(LENGTH(sealed)), 0) FROM tasks WHERE {owner})
                 + (SELECT COALESCE(SUM(LENGTH(sealed_detail)), 0) FROM events WHERE {owner})
                 + (SELECT COALESCE(SUM(LENGTH(sealed_action)), 0) FROM approvals WHERE {owner})
                 + (SELECT COALESCE(SUM(LENGTH(sealed_detail)), 0) FROM notices WHERE {owner})
            """);
        Assert.True(stored > 0);
        Assert.Equal(stored, await SealedBytesAsync(alice));

        // Some of each table past the window, and some not: only what is deleted is given back.
        var askedNotice = "event_kind = 'ApprovalRequested'";
        await database.ExecuteAsync(
            $"""
            UPDATE events SET at = UTC_TIMESTAMP(3) - INTERVAL 400 DAY WHERE {owner} AND sequence <= 3;
            UPDATE notices SET at = UTC_TIMESTAMP(3) - INTERVAL 400 DAY WHERE {owner} AND {askedNotice};
            """);
        var trimmed = await CountAsync(
            $"""
            SELECT (SELECT COALESCE(SUM(LENGTH(sealed_detail)), 0) FROM events WHERE {owner} AND sequence <= 3)
                 + (SELECT COALESCE(SUM(LENGTH(sealed_detail)), 0) FROM notices WHERE {owner} AND {askedNotice})
            """);
        Assert.True(trimmed > 0);

        await new Retention(Db, days: 30).TrimAsync();

        Assert.Equal(stored - trimmed, await SealedBytesAsync(alice));
    }

    // ── bounded rows ────────────────────────────────────────────────────────

    /// <summary>
    /// A removed device keeps its row so the panel can say it was removed, so a register-and-remove loop
    /// grew the table for ever. Rows beyond five times the device limit are pruned, the longest-removed
    /// first, when a device is added; the live device is never among them. Shown red by not pruning.
    /// </summary>
    [Fact]
    public async Task Revoked_device_rows_are_pruned_beyond_five_times_the_limit()
    {
        var alice = await PersonAsync("alice");
        var devices = new DeviceService(Db, Limits.Unlimited with { DevicesPerUser = 1 }, TimeProvider.System);

        var removed = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var id = await devices.RegisterAsync(alice, NewPublicKey(), $"browser {i}", default);
            await devices.RevokeAsync(alice, id, default);
            removed.Add(id);
        }

        var live = await devices.RegisterAsync(alice, NewPublicKey(), "this browser", default);

        var kept = await database.StringsAsync($"SELECT id FROM devices WHERE owner_id = '{alice.UserId}'");
        Assert.Equal(5, kept.Count);
        Assert.Contains(live, kept);
        Assert.Equal(removed[^4..].Order(), kept.Where(id => id != live).Order());
    }

    /// <summary>
    /// The device list is capped at the same five times the limit, the live devices and then the newest
    /// first, so rows left over from before a lower limit do not make one answer as long as the table.
    /// </summary>
    [Fact]
    public async Task The_device_list_is_capped_with_the_live_devices_in_it()
    {
        var alice = await PersonAsync("alice");
        var devices = new DeviceService(Db, Limits.Unlimited with { DevicesPerUser = 1 }, TimeProvider.System);
        var live = await devices.RegisterAsync(alice, NewPublicKey(), "this browser", default);

        // Removed long ago, more of them than the cap: what a lower limit leaves behind.
        await database.ExecuteAsync(
            $"""
            INSERT INTO devices (id, owner_id, public_key, label, created_at, revoked_at)
            WITH RECURSIVE n (i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 8)
            SELECT REPLACE(UUID(), '-', ''), '{alice.UserId}', RANDOM_BYTES(65), CONCAT('old ', i),
                   UTC_TIMESTAMP(3) + INTERVAL i MINUTE, UTC_TIMESTAMP(3) + INTERVAL i MINUTE
            FROM n
            """);

        var listed = await devices.ListAsync(alice, default);

        Assert.Equal(5, listed.Count);
        Assert.Contains(listed, d => d.Id == live && !d.Revoked);
        Assert.Equal(listed.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id, StringComparer.Ordinal), listed);
    }

    /// <summary>
    /// The audit trail has its own window, longer than the panel's history: what happened to an account is
    /// worth keeping for longer than its events, and not for ever. Shown red by not trimming it, and by
    /// trimming it on the history's window.
    /// </summary>
    [Fact]
    public async Task Audit_rows_older_than_ninety_days_are_trimmed()
    {
        var alice = await PersonAsync("alice");

        await database.ExecuteAsync(
            $"""
            INSERT INTO audit (owner_id, at, actor, action, target) VALUES
              ('{alice.UserId}', UTC_TIMESTAMP(3) - INTERVAL 100 DAY, 'operator', 'old', NULL),
              ('{alice.UserId}', UTC_TIMESTAMP(3) - INTERVAL 40 DAY, 'operator', 'month-old', NULL),
              ('{alice.UserId}', UTC_TIMESTAMP(3) - INTERVAL 1 DAY, 'operator', 'recent', NULL)
            """);

        await new Retention(Db, days: 30).TrimAsync();

        var left = await database.StringsAsync(
            $"SELECT action FROM audit WHERE owner_id = '{alice.UserId}' AND actor = 'operator' ORDER BY at");
        Assert.Equal(["month-old", "recent"], left);

        // The audit trail is not the panel's history, so trimming it is not announced as trimmed history.
        Assert.Equal(0, await CountAsync(
            $"SELECT COUNT(*) FROM user_retention WHERE owner_id = '{alice.UserId}' AND trimmed_before IS NOT NULL"));
    }

    // ── configuration ───────────────────────────────────────────────────────

    /// <summary>A limit that is not a whole number of at least one stops the start, and says which.</summary>
    [Theory]
    [InlineData(Limits.HostsSetting, "five")]
    [InlineData(Limits.ActiveRunsSetting, "0")]
    [InlineData(Limits.SealedBytesSetting, "-1")]
    [InlineData(Limits.TasksPerDaySetting, "2.5")]
    public async Task A_limit_that_does_not_parse_stops_the_start(string setting, string value)
    {
        await using var gateway = TestGateway.Create(database,
            configure: builder => builder.UseSetting(setting, value));

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => gateway.CreateClient().GetAsync("/health"));

        Assert.Contains(setting, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Open admission refused to start while the limits were the unlimited placeholder. With limits read from
    /// configuration it starts on the defaults, and a setting replaces its default.
    /// </summary>
    [Fact]
    public async Task Open_admission_starts_once_real_limits_are_configured()
    {
        await using var gateway = TestGateway.Create(database, configure: builder =>
        {
            builder.UseSetting(Admission.Setting, "open");
            builder.UseSetting(Limits.HostsSetting, "7");
        });

        using var response = await gateway.CreateClient().GetAsync("/health");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(Limits.Defaults with { HostsPerUser = 7 }, gateway.Services.GetRequiredService<Limits>());
    }

    private sealed record Refusal(string Code, string Error);

    private sealed record Registered(string Id, string Name, string Token);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
