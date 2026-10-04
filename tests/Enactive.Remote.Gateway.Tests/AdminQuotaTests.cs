namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Gateway;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Administration;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class AdminQuotaTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private Database Db => new(database.ConnectionString);
    private QuotaSettings Settings => new(Db, Limits.Defaults, TimeProvider.System);
    private async Task<AdminSession> Administrator()
    {
        Assert.True(AdminIdentity.TryCreate("https://quota.test", Ids.New(), out var identity));
        var store = new AdminStore(Db, TimeProvider.System);
        await store.GrantAsync(identity!, default);
        return (await store.OpenAsync(identity!, DateTimeOffset.UtcNow, default))!;
    }
    private async Task Set(string? id, string key, string? value, AdminSession admin)
    {
        var view = await Settings.ReadAsync(id, default);
        await Settings.ChangeAsync(id, new(view.Version, new() { [key] = value }, "Quota regression", view.DefaultsVersion), new(admin, view.Version), default);
    }
    private static string Uuid() => Guid.NewGuid().ToString();
    private static string Sealed(string text) => Envelope.Seal(RandomNumberGenerator.GetBytes(32), 1, Encoding.UTF8.GetBytes(text), []);
    private async Task<(UserAccess User, HostAccess Host, UserService Users, HostService Hosts)> Computer()
    {
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        var users = new UserService(Db, Limits.Defaults, TimeProvider.System);
        var hosts = new HostService(Db, limits: Limits.Defaults);
        var registered = await users.RegisterHostAsync(user, "Quota PC", default);
        var host = new HostAccess(registered.Id, user.UserId);
        await hosts.SyncAsync(host, [new WorkspaceRef("workspace", Sealed("Work"))]);
        return (user, host, users, hosts);
    }
    [Fact]
    public async Task Resolution_reads_committed_defaults_even_after_a_transaction_snapshot_was_established()
    {
        var user = await TestAccounts.CreateAsync(database, Ids.New()); var admin = await Administrator();
        try
        {
            await Db.InTransactionAsync(async (c, tx) =>
            {
                await Quota.LockAccountAsync(c, tx, user.UserId);
                await c.ReadOneAsync(tx, "SELECT COUNT(*) FROM users", r => r.GetInt64(0));
                await Set(null, "HostsPerUser", "1", admin);
                Assert.Equal(1, (await QuotaSettings.ResolveAsync(c, tx, user.UserId, Limits.Defaults)).HostsPerUser);
            }, default);
        }
        finally { await Set(null, "HostsPerUser", null, admin); }
    }
    [Fact]
    public async Task Connection_budget_uses_updated_host_quota_and_preserves_reconnects()
    {
        var (user, host, _, hosts) = await Computer(); var admin = await Administrator();
        var connections = new HostConnections(Limits.Defaults);
        await Set(user.UserId, "HostsPerUser", "1", admin);
        var limits = await hosts.EffectiveLimitsAsync(host, default);
        connections.TryAdd("a", "host-a", user.UserId, () => { }, out _, limits);
        connections.TryAdd("b", "host-b", user.UserId, () => { }, out _, limits);
        Assert.Equal(HostConnections.Refusal.AccountFull, connections.TryAdd("c", "host-c", user.UserId, () => { }, out _, limits));
        Assert.Equal(HostConnections.Refusal.None, connections.TryAdd("a2", "host-a", user.UserId, () => { }, out _, limits));
        await Set(user.UserId, "HostsPerUser", "2", admin);
        Assert.Equal(HostConnections.Refusal.None, connections.TryAdd("c", "host-c", user.UserId, () => { }, out _, await hosts.EffectiveLimitsAsync(host, default)));
    }
    [Fact]
    public async Task Defaults_survive_a_new_service_and_inherit_without_copying_into_accounts()
    {
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        var admin = await Administrator();
        try
        {
            await Set(null, "HostsPerUser", "1", admin);
            var restarted = new UserService(new Database(database.ConnectionString), Limits.Defaults, TimeProvider.System);
            await restarted.RegisterHostAsync(user, "First", default);
            await Assert.ThrowsAsync<GatewayFault>(() => restarted.RegisterHostAsync(user, "Second", default));
            await Set(user.UserId, "HostsPerUser", "3", admin);
            await Set(null, "HostsPerUser", "2", admin);
            Assert.Equal("3", (await Settings.ReadAsync(user.UserId, default)).Items[0].Effective);
            await Set(user.UserId, "HostsPerUser", null, admin);
            var item = (await Settings.ReadAsync(user.UserId, default)).Items[0];
            Assert.Equal("2", item.Effective); Assert.Equal("default", item.Source); Assert.Null(item.Override);
        }
        finally { await Set(null, "HostsPerUser", null, admin); }
    }
    [Fact]
    public async Task Concurrent_edits_have_one_winner_and_audited_reason_and_values()
    {
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        var admin = await Administrator();
        var view = await Settings.ReadAsync(user.UserId, default);
        var outcomes = await Task.WhenAll(new[] { "1", "2" }.Select(async value =>
        {
            try { await Settings.ChangeAsync(user.UserId, new(view.Version, new() { ["HostsPerUser"] = value }, "Capacity", view.DefaultsVersion), new(admin, view.Version), default); return true; }
            catch (GatewayFault e) when (e.Status == 409) { return false; }
        }));
        Assert.Single(outcomes, x => x);
        var audits = await database.StringsAsync($"SELECT detail FROM administrator_audit WHERE action = 'quota.changed' AND target = '{user.UserId}'");
        var audit = JsonDocument.Parse(Assert.Single(audits));
        Assert.Equal("Capacity", audit.RootElement.GetProperty("reason").GetString());
        Assert.Empty(audit.RootElement.GetProperty("before").EnumerateObject());
        Assert.Single(audit.RootElement.GetProperty("after").EnumerateObject());
    }
    [Fact]
    public async Task Shared_default_change_invalidates_a_user_form_and_reset_keeps_revision()
    {
        var user = await TestAccounts.CreateAsync(database, Ids.New()); var admin = await Administrator();
        var old = await Settings.ReadAsync(user.UserId, default);
        try
        {
            await Set(null, "HostsPerUser", "4", admin);
            var failure = await Assert.ThrowsAsync<GatewayFault>(() => Settings.ChangeAsync(user.UserId,
                new(old.Version, new() { ["HostsPerUser"] = "3" }, "Old form", old.DefaultsVersion), new(admin, old.Version), default));
            Assert.Equal(409, failure.Status);
        }
        finally { await Set(null, "HostsPerUser", null, admin); }
        await Set(user.UserId, "HostsPerUser", "1", admin);
        await Set(user.UserId, "HostsPerUser", null, admin);
        Assert.Equal(2, (await Settings.ReadAsync(user.UserId, default)).Version);
    }
    [Fact]
    public async Task Device_and_invitation_limits_apply_on_existing_services_and_live_devices_remain_visible()
    {
        var (user, host, _, _) = await Computer(); var admin = await Administrator();
        var devices = new DeviceService(Db, Limits.Defaults, TimeProvider.System);
        byte[] Key() { using var key = P256.Generate(); return P256.PublicRaw(key); }
        for (var i = 0; i < 6; i++) await devices.RegisterAsync(user, Key(), "Browser " + i, default);
        await Set(user.UserId, "DevicesPerUser", "1", admin);
        Assert.Equal(6, (await devices.ListAsync(user, default)).Count);
        await Assert.ThrowsAsync<GatewayFault>(() => devices.RegisterAsync(user, Key(), "Extra", default));
        await Set(user.UserId, "OpenInvitesPerUser", "1", admin);
        await devices.CreateInviteAsync(host, Ids.New(), default);
        await Assert.ThrowsAsync<GatewayFault>(() => devices.CreateInviteAsync(host, Ids.New(), default));
        var browser = (await devices.ListAsync(user, default))[0];
        await Assert.ThrowsAsync<GatewayFault>(() => devices.CreateInviteAsync(new DeviceAccess(browser.Id, user.UserId), Ids.New(), default));
        await devices.RevokeAsync(user, browser.Id, default);
        Assert.Equal(5, (await devices.ListAsync(user, default)).Count(x => !x.Revoked));
    }
    [Fact]
    public async Task Task_and_run_limits_are_dynamic_and_terminal_reports_survive_storage_reduction()
    {
        var (user, host, users, hosts) = await Computer(); var admin = await Administrator();
        var task = Uuid();
        await users.CreateTaskAsync(user, task, host.HostId, "workspace", Sealed("Do work"), default);
        await Set(user.UserId, "TasksPerDay", "1", admin);
        await Assert.ThrowsAsync<GatewayFault>(() => users.CreateTaskAsync(user, Uuid(), host.HostId, "workspace", Sealed("Extra"), default));
        var command = await users.StartAsync(user, task, Uuid(), Sealed("Start"), default);
        var run = RemoteJson.Deserialize<StartTaskPayload>(command.Payload).RunId;
        await Set(user.UserId, "ActiveRunsPerUser", "1", admin);
        await Assert.ThrowsAsync<GatewayFault>(() => users.StartAsync(user, task, Uuid(), Sealed("More"), default));
        await hosts.PublishAsync(host, new HostEvent(Uuid(), run, 1, RemoteEventKind.Running));
        await Set(user.UserId, "SealedBytesPerUser", "1", admin);
        var progress = await Assert.ThrowsAsync<GatewayFault>(() => hosts.PublishAsync(host, new HostEvent(Uuid(), run, 2, RemoteEventKind.Progress, Sealed("Progress"))));
        Assert.Equal(FaultCode.StorageFull, progress.Code);
        var terminal = new HostEvent(Uuid(), run, 3, RemoteEventKind.Completed, Sealed("Done"));
        await hosts.PublishAsync(host, terminal);
        var held = await database.ScalarLongAsync($"SELECT sealed_bytes FROM users WHERE id = '{user.UserId}'");
        await hosts.PublishAsync(host, terminal);
        Assert.Equal(held, await database.ScalarLongAsync($"SELECT sealed_bytes FROM users WHERE id = '{user.UserId}'"));
        Assert.Equal("Completed", Assert.Single(await database.StringsAsync($"SELECT status FROM runs WHERE id = '{run}'")));
        await Assert.ThrowsAsync<GatewayFault>(() => users.StartAsync(user, task, Uuid(), Sealed("More"), default));
    }
    [Fact]
    public async Task Full_queue_accepts_one_cancellation_per_existing_run_and_reports_per_host_usage()
    {
        var (user, host, users, hosts) = await Computer(); var admin = await Administrator();
        var task = Uuid(); await users.CreateTaskAsync(user, task, host.HostId, "workspace", Sealed("Work"), default);
        var command = await users.StartAsync(user, task, Uuid(), Sealed("Start"), default);
        var run = RemoteJson.Deserialize<StartTaskPayload>(command.Payload).RunId;
        await Set(user.UserId, "QueuedCommandsPerHost", "1", admin);
        await Assert.ThrowsAsync<GatewayFault>(() => users.StartAsync(user, task, Uuid(), Sealed("Another"), default));
        await users.CancelAsync(user, run, Uuid(), Sealed("Cancel"), default);
        await Assert.ThrowsAsync<GatewayFault>(() => users.CancelAsync(user, run, Uuid(), Sealed("Cancel again"), default));
        var queue = Assert.Single((await Settings.QueuesAsync(user.UserId, null, default)).Items);
        Assert.Equal(host.HostId, queue.HostId); Assert.Equal(1, queue.Pending); Assert.Equal(1, queue.Cancellations);
        await users.RevokeHostAsync(user, host.HostId, default);
        Assert.Empty((await Settings.QueuesAsync(user.UserId, null, default)).Items);
    }
    [Fact]
    public async Task Existing_service_observes_override_and_reset_without_restart()
    {
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        var service = new UserService(Db, Limits.Defaults, TimeProvider.System);
        var admin = await Administrator();
        await Set(user.UserId, "HostsPerUser", "1", admin);
        await service.RegisterHostAsync(user, "First", default);
        await Assert.ThrowsAsync<GatewayFault>(() => service.RegisterHostAsync(user, "Second", default));
        await Set(user.UserId, "HostsPerUser", null, admin);
        await service.RegisterHostAsync(user, "Second", default);
        Assert.Equal("startup", (await Settings.ReadAsync(user.UserId, default)).Items.Single(q => q.Key == "HostsPerUser").Source);
    }
    [Fact]
    public async Task Concurrent_creation_and_lowering_preserve_existing_resources()
    {
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        var admin = await Administrator();
        await Set(user.UserId, "HostsPerUser", "2", admin);
        var service = new UserService(Db, Limits.Defaults, TimeProvider.System);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async n =>
        {
            try { await new UserService(new Database(database.ConnectionString), Limits.Defaults, TimeProvider.System).RegisterHostAsync(user, "PC " + n, default); return true; }
            catch (GatewayFault) { return false; }
        }));
        Assert.Equal(2, results.Count(x => x));
        await Set(user.UserId, "HostsPerUser", "1", admin);
        var item = (await Settings.ReadAsync(user.UserId, default)).Items.Single(q => q.Key == "HostsPerUser");
        Assert.Equal("2", item.Usage); Assert.True(item.OverLimit);
        Assert.Equal(2, await database.ScalarLongAsync($"SELECT COUNT(*) FROM hosts WHERE owner_id = '{user.UserId}'"));
        await Assert.ThrowsAsync<GatewayFault>(() => service.RegisterHostAsync(user, "Third", default));
    }
    [Fact]
    public async Task Stale_edits_and_stale_authority_cannot_change_limits()
    {
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        var admin = await Administrator();
        var view = await Settings.ReadAsync(user.UserId, default);
        var request = new QuotaChange(view.Version, new() { ["DevicesPerUser"] = "2" }, "Test", view.DefaultsVersion);
        await Settings.ChangeAsync(user.UserId, request, new(admin, view.Version), default);
        var conflict = await Assert.ThrowsAsync<GatewayFault>(() => Settings.ChangeAsync(user.UserId, request, new(admin, view.Version), default));
        Assert.Equal(409, conflict.Status);
        await database.ExecuteAsync($"UPDATE administrator_sessions SET authenticated_at = UTC_TIMESTAMP(3) - INTERVAL 6 MINUTE WHERE id = '{admin.SessionId}'");
        await Assert.ThrowsAsync<GatewayFault>(() => Set(user.UserId, "DevicesPerUser", "3", admin));
        Assert.Equal("2", (await Settings.ReadAsync(user.UserId, default)).Items.Single(q => q.Key == "DevicesPerUser").Effective);
    }
    [Theory]
    [InlineData("HostsPerUser", "0")]
    [InlineData("HostsPerUser", "2147483648")]
    [InlineData("SealedBytesPerUser", "9223372036854775808")]
    [InlineData("DevicesPerUser", "-1")]
    [InlineData("Unknown", "1")]
    [InlineData("TasksPerDay", "1.5")]
    public async Task Invalid_values_are_rejected(string key, string value)
    {
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        var admin = await Administrator();
        await Assert.ThrowsAsync<GatewayFault>(() => Set(user.UserId, key, value, admin));
    }
    [Fact]
    public async Task Audit_failure_rolls_back_quota_and_revision()
    {
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        var admin = await Administrator();
        var before = await Settings.ReadAsync(user.UserId, default);
        await database.ExecuteAsync("""
            CREATE TRIGGER fail_quota_audit BEFORE INSERT ON administrator_audit FOR EACH ROW
            BEGIN IF NEW.action = 'quota.changed' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected failure'; END IF; END
            """);
        try { await Assert.ThrowsAsync<MySqlException>(() => Set(user.UserId, "HostsPerUser", "1", admin)); }
        finally { await database.ExecuteAsync("DROP TRIGGER fail_quota_audit"); }
        var after = await Settings.ReadAsync(user.UserId, default);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Items, after.Items);
    }
}
