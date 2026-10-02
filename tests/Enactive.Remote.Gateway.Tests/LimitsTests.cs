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

    /// <summary>
    /// The size of every sealed field the account stores that is charged: what its total must equal. A start
    /// command's whole payload is charged, since it carries a copy of the task.
    /// </summary>
    private Task<long> StoredAsync(UserAccess user)
    {
        var owner = $"owner_id = '{user.UserId}'";
        return CountAsync(
            $"""
            SELECT (SELECT COALESCE(SUM(LENGTH(sealed)), 0) FROM tasks WHERE {owner})
                 + (SELECT COALESCE(SUM(LENGTH(sealed_summary)), 0) FROM runs WHERE {owner})
                 + (SELECT COALESCE(SUM(LENGTH(sealed_detail)), 0) FROM events WHERE {owner})
                 + (SELECT COALESCE(SUM(LENGTH(sealed_action)), 0) FROM approvals WHERE {owner})
                 + (SELECT COALESCE(SUM(LENGTH(sealed_detail)), 0) FROM notices WHERE {owner})
                 + (SELECT COALESCE(SUM(LENGTH(payload)), 0) FROM commands WHERE {owner} AND kind = 'StartTask')
            """);
    }

    private static string RunOf(HostCommand start) => RemoteJson.Deserialize<StartTaskPayload>(start.Payload).RunId;

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
        var host = await ComputerAsync(users, new HostService(Db), alice);

        var tasks = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var taskId = Uuid();
            await users.CreateTaskAsync(alice, taskId, host.HostId, "workspace-1", Sealed($"task {i}"), default);
            tasks.Add(taskId);
        }

        var started = await users.StartAsync(alice, tasks[0], Uuid(), Sealed("start"), default);
        var secondId = Uuid();
        var secondSeal = Sealed("start");
        var second = await users.StartAsync(alice, tasks[1], secondId, secondSeal, default);

        var refused = await Assert.ThrowsAsync<GatewayFault>(
            () => users.StartAsync(alice, tasks[2], Uuid(), Sealed("start"), default));
        Assert.Equal(FaultCode.QuotaExceeded, refused.Code);
        Assert.Equal(409, refused.Status);
        Assert.Contains("2 ", refused.Message, StringComparison.Ordinal);

        // A retry of a start already queued is answered with the same command at the limit, not refused: its
        // reply was lost, and the run it asked for is one of the two.
        var retried = await users.StartAsync(alice, tasks[1], secondId, secondSeal, default);
        Assert.Equal((second.Id, RunOf(second)), (retried.Id, RunOf(retried)));

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
        var host = await ComputerAsync(users, new HostService(Db), alice);

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
    /// Device commands have an allowance of their own, two for each device the account may hold.
    /// </summary>
    [Fact]
    public async Task Queued_commands_per_computer_stop_at_the_limit()
    {
        var alice = await PersonAsync("alice");
        var limits = Limits.Unlimited with { QueuedCommandsPerHost = 1, DevicesPerUser = 1 };
        var users = new UserService(Db, limits, TimeProvider.System);
        var hosts = new HostService(Db);
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

    /// <summary>
    /// A removal is never refused because the computer's queue is full. A computer that was off while the
    /// person kept asking it things - or while a thief did - came back with a queue at its limit, and the
    /// removal of the lost phone was the one request the gateway turned away.
    /// </summary>
    [Fact]
    public async Task A_full_queue_still_takes_a_removal()
    {
        var alice = await PersonAsync("alice");
        var users = new UserService(Db, Limits.Unlimited with { QueuedCommandsPerHost = 1 }, TimeProvider.System);
        var hosts = new HostService(Db);
        var host = await ComputerAsync(users, hosts, alice);
        var first = Uuid();
        await users.CreateTaskAsync(alice, first, host.HostId, "workspace-1", Sealed("first"), default);
        await users.StartAsync(alice, first, Uuid(), Sealed("start"), default);
        var second = Uuid();
        await users.CreateTaskAsync(alice, second, host.HostId, "workspace-1", Sealed("second"), default);

        var full = await Assert.ThrowsAsync<GatewayFault>(
            () => users.StartAsync(alice, second, Uuid(), Sealed("start"), default));
        Assert.Equal(FaultCode.QuotaExceeded, full.Code);

        var removal = await users.SendDeviceCommandAsync(
            alice, host.HostId, CommandKind.RevokeDevice, Uuid(), Sealed("device"), default);
        Assert.Equal(CommandStatus.PendingDelivery, removal.Status);
    }

    /// <summary>
    /// A computer that is revoked can never report again, and its runs cannot be stopped from here: they are
    /// ended when it is revoked, their open requests withdrawn, and a run that is somehow still open on a
    /// revoked computer holds no place. Shown red by revoking without ending the runs and counting every
    /// computer's runs: the account can then start nothing, for good.
    /// </summary>
    [Fact]
    public async Task A_run_on_a_revoked_computer_does_not_hold_a_place()
    {
        var alice = await PersonAsync("alice");
        var users = new UserService(Db, Limits.Unlimited with { ActiveRunsPerUser = 1 }, TimeProvider.System);
        var hosts = new HostService(Db);
        var dead = await ComputerAsync(users, hosts, alice);
        var live = await ComputerAsync(users, hosts, alice);

        var deadTask = Uuid();
        await users.CreateTaskAsync(alice, deadTask, dead.HostId, "workspace-1", Sealed("on the old PC"), default);
        var liveTask = Uuid();
        await users.CreateTaskAsync(alice, liveTask, live.HostId, "workspace-1", Sealed("on the new PC"), default);

        var runId = RunOf(await users.StartAsync(alice, deadTask, Uuid(), Sealed("start"), default));
        await hosts.PublishAsync(dead, new HostEvent(Uuid(), runId, 1, RemoteEventKind.Running));
        await hosts.PublishAsync(dead, new HostEvent(Uuid(), runId, 2, RemoteEventKind.ApprovalRequested,
            Sealed("May I?"), new ApprovalRequest("approval-1", "call-1", "hash-1", true, Sealed("rm -rf"))));

        await users.RevokeHostAsync(alice, dead.HostId, default);

        Assert.Equal("Interrupted", Assert.Single(await database.StringsAsync(
            $"SELECT status FROM runs WHERE id = '{runId}' AND ended_at IS NOT NULL AND sealed_summary IS NULL")));
        Assert.Equal("Invalidated", Assert.Single(await database.StringsAsync(
            $"SELECT status FROM approvals WHERE run_id = '{runId}'")));

        // Even a run left open on the revoked computer does not count.
        await database.ExecuteAsync($"UPDATE runs SET status = 'Running', ended_at = NULL WHERE id = '{runId}'");

        await users.StartAsync(alice, liveTask, Uuid(), Sealed("start"), default);
    }

    /// <summary>
    /// A start copies the task into its command, so every start stores the task again, and every start is
    /// charged for it. Shown red by not charging the command: a script restarting one large task stored it
    /// without limit.
    /// </summary>
    [Fact]
    public async Task A_restarted_task_is_charged_for_each_start()
    {
        var alice = await PersonAsync("alice");
        var users = new UserService(Db, Limits.Unlimited, TimeProvider.System);
        var hosts = new HostService(Db);
        var host = await ComputerAsync(users, hosts, alice);
        var taskId = Uuid();
        await users.CreateTaskAsync(alice, taskId, host.HostId, "workspace-1", Sealed(new string('x', 2000)), default);
        var before = await SealedBytesAsync(alice);

        var first = await users.StartAsync(alice, taskId, Uuid(), Sealed("start"), default);
        await hosts.PublishAsync(host, new HostEvent(Uuid(), RunOf(first), 1, RemoteEventKind.Running));
        await hosts.PublishAsync(host, new HostEvent(Uuid(), RunOf(first), 2, RemoteEventKind.Completed));
        var second = await users.StartAsync(alice, taskId, Uuid(), Sealed("start"), default);

        Assert.Equal(before + first.Payload.Length + second.Payload.Length, await SealedBytesAsync(alice));
        Assert.Equal(await StoredAsync(alice), await SealedBytesAsync(alice));
    }

    // ── sealed bytes ────────────────────────────────────────────────────────

    /// <summary>
    /// The person's work is what the byte limit refuses: a task, and a start, whose command carries a copy of
    /// the task. Refused whole, and in a sentence that says the limit in megabytes and what frees the space.
    /// Shown red by not charging: the total stays at zero and nothing is refused.
    /// </summary>
    [Fact]
    public async Task Sealed_bytes_stop_at_the_limit()
    {
        var alice = await PersonAsync("alice");
        var sealedTask = Sealed("Run the tests");
        var limits = Limits.Unlimited with { SealedBytesPerUser = sealedTask.Length + 10 };
        var users = new UserService(Db, limits, TimeProvider.System);
        var host = await ComputerAsync(users, new HostService(Db), alice);

        var taskId = Uuid();
        await users.CreateTaskAsync(alice, taskId, host.HostId, "workspace-1", sealedTask, default);
        Assert.Equal(sealedTask.Length, await SealedBytesAsync(alice));

        var refused = await Assert.ThrowsAsync<GatewayFault>(() =>
            users.CreateTaskAsync(alice, Uuid(), host.HostId, "workspace-1", Sealed("Another"), default));
        Assert.Equal(FaultCode.QuotaExceeded, refused.Code);
        Assert.Equal(409, refused.Status);
        Assert.Contains(" MB ", refused.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"older runs are removed after {Retention.DefaultDays} days", refused.Message, StringComparison.Ordinal);

        var start = await Assert.ThrowsAsync<GatewayFault>(
            () => users.StartAsync(alice, taskId, Uuid(), Sealed("start"), default));
        Assert.Equal(FaultCode.QuotaExceeded, start.Code);

        // Refused whole: no run, no command, no bytes.
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM runs WHERE task_id = '{taskId}'"));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM commands WHERE owner_id = '{alice.UserId}'"));
        Assert.Equal(sealedTask.Length, await SealedBytesAsync(alice));
    }

    /// <summary>
    /// What a computer reports about a run is always taken, and counted, even past the limit: the run must be
    /// able to end. The person's next start is what is refused. Shown red by refusing the computer's writes at
    /// the limit: its events - the run's end among them - are then refused with the code a computer waits out,
    /// and the run stays "running" for good.
    /// </summary>
    [Fact]
    public async Task A_full_account_still_hears_its_runs_end()
    {
        var alice = await PersonAsync("alice");
        var setup = new UserService(Db, Limits.Unlimited, TimeProvider.System);
        var hosts = new HostService(Db);
        var host = await ComputerAsync(setup, hosts, alice);
        var taskId = Uuid();
        await setup.CreateTaskAsync(alice, taskId, host.HostId, "workspace-1", Sealed("Run the tests"), default);
        var runId = RunOf(await setup.StartAsync(alice, taskId, Uuid(), Sealed("start"), default));

        // Full: the limit is exactly what it holds.
        var full = Limits.Unlimited with { SealedBytesPerUser = await SealedBytesAsync(alice) };

        await hosts.PublishAsync(host, new HostEvent(Uuid(), runId, 1, RemoteEventKind.Running));
        await hosts.PublishAsync(host, new HostEvent(Uuid(), runId, 2, RemoteEventKind.Progress, Sealed("Built")));
        await hosts.PublishAsync(host, new HostEvent(Uuid(), runId, 3, RemoteEventKind.Completed, Sealed("Done")));

        Assert.Equal("Completed", Assert.Single(
            await database.StringsAsync($"SELECT status FROM runs WHERE id = '{runId}'")));
        Assert.Equal(3, await CountAsync($"SELECT COUNT(*) FROM events WHERE run_id = '{runId}'"));
        Assert.True(await SealedBytesAsync(alice) > full.SealedBytesPerUser);
        Assert.Equal(await StoredAsync(alice), await SealedBytesAsync(alice));

        var refused = await Assert.ThrowsAsync<GatewayFault>(() =>
            new UserService(Db, full, TimeProvider.System).StartAsync(alice, taskId, Uuid(), Sealed("start"), default));
        Assert.Equal(FaultCode.QuotaExceeded, refused.Code);
        Assert.Equal(409, refused.Status);
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
        var hosts = new HostService(Db);
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
        var stored = await StoredAsync(alice);
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

    /// <summary>
    /// A run that ended before the window goes whole: its events, requests, notices, commands and summary with
    /// it, and every byte it was charged comes back. Its task, made inside the window, stays. Shown red by
    /// trimming only events and notices: the run's request, summary and start command were then kept, and
    /// charged, for good.
    /// </summary>
    [Fact]
    public async Task Retention_removes_an_ended_run_with_everything_it_owns_and_returns_its_bytes()
    {
        var alice = await PersonAsync("alice");
        var users = new UserService(Db, Limits.Unlimited, TimeProvider.System);
        var hosts = new HostService(Db);
        var host = await ComputerAsync(users, hosts, alice);
        var taskId = Uuid();
        var sealedTask = Sealed("Run the tests");
        await users.CreateTaskAsync(alice, taskId, host.HostId, "workspace-1", sealedTask, default);
        var runId = RunOf(await users.StartAsync(alice, taskId, Uuid(), Sealed("start"), default));

        await hosts.PublishAsync(host, new HostEvent(Uuid(), runId, 1, RemoteEventKind.Running));
        await hosts.PublishAsync(host, new HostEvent(Uuid(), runId, 2, RemoteEventKind.ApprovalRequested,
            Sealed("May I?"), new ApprovalRequest("approval-1", "call-1", "hash-1", true, Sealed("dotnet test"))));
        await users.DecideAsync(alice, "approval-1", host.HostId, Uuid(), RemoteDecision.Allow, "hash-1",
            Sealed("allow"), default);
        await hosts.PublishAsync(host, new HostEvent(Uuid(), runId, 3, RemoteEventKind.Completed, Sealed("Done")));

        // Only the run is old: its events and notices are recent, so they go because the run does.
        await database.ExecuteAsync(
            $"UPDATE runs SET ended_at = UTC_TIMESTAMP(3) - INTERVAL 400 DAY WHERE id = '{runId}'");

        await new Retention(Db, days: 30).TrimAsync();

        foreach (var table in new[] { "runs WHERE id", "events WHERE run_id", "approvals WHERE run_id",
                     "notices WHERE run_id", "commands WHERE run_id" })
        {
            Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM {table} = '{runId}'"));
        }

        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM tasks WHERE id = '{taskId}'"));
        Assert.Equal(sealedTask.Length, await SealedBytesAsync(alice));
        Assert.Equal(1, await CountAsync(
            $"SELECT COUNT(*) FROM user_retention WHERE owner_id = '{alice.UserId}' AND trimmed_before IS NOT NULL"));
    }

    /// <summary>
    /// A task made before the window with no run left goes, and its bytes come back; one with a run still
    /// going stays, and so does a recent one. Shown red by never deleting tasks: an account that filled up with
    /// tasks stayed full.
    /// </summary>
    [Fact]
    public async Task A_task_with_no_run_is_removed_after_the_window()
    {
        var alice = await PersonAsync("alice");
        var users = new UserService(Db, Limits.Unlimited, TimeProvider.System);
        var host = await ComputerAsync(users, new HostService(Db), alice);

        var unused = Uuid();
        var running = Uuid();
        var recent = Uuid();
        foreach (var id in new[] { unused, running, recent })
        {
            await users.CreateTaskAsync(alice, id, host.HostId, "workspace-1", Sealed($"task {id}"), default);
        }

        await users.StartAsync(alice, running, Uuid(), Sealed("start"), default);
        await database.ExecuteAsync(
            $"UPDATE tasks SET created_at = UTC_TIMESTAMP(3) - INTERVAL 400 DAY WHERE id IN ('{unused}', '{running}')");

        await new Retention(Db, days: 30).TrimAsync();

        Assert.Equal(new[] { recent, running }.Order(), (await database.StringsAsync(
            $"SELECT id FROM tasks WHERE owner_id = '{alice.UserId}'")).Order());
        Assert.Equal(await StoredAsync(alice), await SealedBytesAsync(alice));
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
        var (hostId, _, _) = await new UserService(Db, Limits.Unlimited, TimeProvider.System)
            .RegisterHostAsync(alice, "Studio PC", default);

        var removed = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var id = await devices.RegisterAsync(alice, NewPublicKey(), $"browser {i}", default);
            await devices.RevokeAsync(alice, id, default);
            removed.Add(id);

            // A grant that landed after the removal, and the invitation the device answered: rows the
            // device's own row takes with it when it goes.
            var invite = Guid.NewGuid().ToString("N");
            await database.ExecuteAsync(
                $"""
                INSERT INTO grants (owner_id, host_id, device_id, epoch, grant_json, created_at)
                  VALUES ('{alice.UserId}', '{hostId}', '{id}', 1, 'grant', UTC_TIMESTAMP(3));
                INSERT INTO invites (id, owner_id, created_at, expires_at, consumed_at)
                  VALUES ('{invite}', '{alice.UserId}', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3), UTC_TIMESTAMP(3));
                INSERT INTO enrollments (invite_id, owner_id, device_id, mac, created_at)
                  VALUES ('{invite}', '{alice.UserId}', '{id}', 'mac', UTC_TIMESTAMP(3));
                """);
        }

        var live = await devices.RegisterAsync(alice, NewPublicKey(), "this browser", default);

        var kept = await database.StringsAsync($"SELECT id FROM devices WHERE owner_id = '{alice.UserId}'");
        Assert.Equal(5, kept.Count);
        Assert.Contains(live, kept);
        Assert.Equal(removed[^4..].Order(), kept.Where(id => id != live).Order());

        foreach (var table in new[] { "grants", "enrollments" })
        {
            Assert.Equal(removed[^4..].Order(), (await database.StringsAsync(
                $"SELECT device_id FROM {table} WHERE owner_id = '{alice.UserId}'")).Order());
        }
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
