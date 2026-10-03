namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Gateway;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Administration;
using Enactive.Remote.Gateway.Storage;
using Enactive.Remote.Gateway.Services;
using MySqlConnector;

public sealed class AdminAccessTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private Database Db => new(database.ConnectionString);
    private AdministrationService Service => new(Db, TimeProvider.System);
    private async Task<(AdminIdentity Identity, AdminSession Session)> Administrator()
    {
        Assert.True(AdminIdentity.TryCreate("https://issuer.test", Ids.New(), out var identity));
        var store = new AdminStore(Db, TimeProvider.System);
        await store.GrantAsync(identity!, default);
        var session = await store.OpenAsync(identity!, DateTimeOffset.UtcNow, default);
        return (identity!, Assert.IsType<AdminSession>(session));
    }

    [Fact]
    public async Task Disable_and_enable_record_the_actual_actor_and_state_transition()
    {
        var admin = await Administrator();
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        await Service.DisableAccountAsync(user.UserId, default, new(admin.Session, 1));
        await Service.EnableAccountAsync(user.UserId, default, new(admin.Session, 2));
        await using var connection = await Db.OpenAsync();
        var rows = await connection.ReadAllAsync(null, """
            SELECT action, detail FROM administrator_audit WHERE actor = @actor AND target = @target ORDER BY id
            """, r =>
            {
                using var detail = System.Text.Json.JsonDocument.Parse(r.GetString("detail"));
                return r.GetString("action") + "|" + detail.RootElement.GetProperty("before").GetString()
                    + "|" + detail.RootElement.GetProperty("after").GetString();
            }, ("@actor", "admin:" + admin.Session.AdministratorId), ("@target", user.UserId));
        Assert.Equal(new[] { "account.disabled|Active|Disabled", "account.enabled|Disabled|Active" }, rows);
        Assert.False(await new SessionStore(Db, TimeProvider.System).ValidAsync(user.UserId, user.SessionId, 1, default));
    }

    [Fact]
    public async Task Enabling_an_active_account_is_a_no_op_and_does_not_sign_the_owner_out()
    {
        var admin = await Administrator();
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        Assert.True(await Service.EnableAccountAsync(user.UserId, default, new(admin.Session, 1)));
        Assert.True(await Service.EnableAccountAsync(user.UserId, default, new(admin.Session, 1)));
        Assert.True(await new SessionStore(Db, TimeProvider.System).ValidAsync(user.UserId, user.SessionId, 1, default));
        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM administrator_audit WHERE target = '{user.UserId}'"));
    }

    [Fact]
    public async Task Stale_retry_cannot_revoke_a_new_session_and_audit_survives_account_deletion()
    {
        var admin = await Administrator();
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        Assert.True(await Service.RevokeSessionsAsync(user.UserId, default, new(admin.Session, 1)));
        var sessions = new SessionStore(Db, TimeProvider.System);
        var fresh = await sessions.OpenAsync(user.UserId, TestAccounts.Provider, default);
        var error = await Assert.ThrowsAsync<GatewayFault>(() => Service.RevokeSessionsAsync(user.UserId, default, new(admin.Session, 1)));
        Assert.Equal(409, error.Status);
        Assert.True(await sessions.ValidAsync(user.UserId, fresh.SessionId, 2, default));
        await database.ExecuteAsync($"DELETE FROM users WHERE id = '{user.UserId}'");
        Assert.Equal(1, await database.ScalarLongAsync($"SELECT COUNT(*) FROM administrator_audit WHERE actor = 'admin:{admin.Session.AdministratorId}' AND action = 'sessions.revoked' AND target = '{user.UserId}'"));
    }

    [Fact]
    public async Task Administrator_revocation_and_stale_authentication_are_rechecked_inside_the_mutation()
    {
        var admin = await Administrator();
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        await database.ExecuteAsync($"UPDATE administrator_sessions SET authenticated_at = UTC_TIMESTAMP(3) - INTERVAL 6 MINUTE WHERE id = '{admin.Session.SessionId}'");
        await Assert.ThrowsAsync<GatewayFault>(() => Service.DisableAccountAsync(user.UserId, default, new(admin.Session, 1)));
        await new AdminStore(Db, TimeProvider.System).RevokeAsync(admin.Identity, default);
        await Assert.ThrowsAsync<GatewayFault>(() => Service.EnableAccountAsync(user.UserId, default, new(admin.Session, 1)));
        Assert.Equal(1, await database.ScalarLongAsync($"SELECT security_version FROM users WHERE id = '{user.UserId}'"));
    }

    [Fact]
    public async Task Failed_independent_audit_rolls_back_disable_sessions_and_queue_withdrawal()
    {
        var admin = await Administrator();
        var user = await TestAccounts.CreateAsync(database, Ids.New());
        var host = await new UserService(Db, Limits.Unlimited, TimeProvider.System).RegisterHostAsync(user, "Test", default);
        var command = Ids.New();
        await database.ExecuteAsync("""
            INSERT INTO commands (owner_id, id, host_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES (@owner, @id, @host, 'CancelRun', '{}', SHA2(@id, 256), 'PendingDelivery', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL 1 DAY)
            """, ("@owner", user.UserId), ("@id", command), ("@host", host.Id));
        await database.ExecuteAsync("""
            CREATE TRIGGER fail_access_audit BEFORE INSERT ON administrator_audit FOR EACH ROW
            BEGIN
              IF NEW.action = 'account.disabled' THEN
                SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected audit failure';
              END IF;
            END
            """);
        try { await Assert.ThrowsAsync<MySqlException>(() => Service.DisableAccountAsync(user.UserId, default, new(admin.Session, 1))); }
        finally { await database.ExecuteAsync("DROP TRIGGER fail_access_audit"); }
        Assert.Equal("Active", Assert.Single(await database.StringsAsync($"SELECT status FROM users WHERE id = '{user.UserId}'")));
        Assert.True(await new SessionStore(Db, TimeProvider.System).ValidAsync(user.UserId, user.SessionId, 1, default));
        Assert.Equal("PendingDelivery", Assert.Single(await database.StringsAsync($"SELECT status FROM commands WHERE id = '{command}'")));
        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM audit WHERE owner_id = '{user.UserId}' AND action = 'account.disabled'"));
    }

    [Fact]
    public async Task Concurrent_opposing_decisions_have_one_winner_and_preserve_the_full_identity()
    {
        var first = await Administrator(); var second = await Administrator();
        Assert.True(AdmissionIdentity.TryParse("github:" + new string('x', 255), out var identity));
        await database.ExecuteAsync("INSERT INTO admissions (provider, subject, display, state, requested_at) VALUES ('github', @subject, 'Test', 'Waiting', UTC_TIMESTAMP(3))", ("@subject", identity.Subject));
        async Task<int> Decide(AdminSession session, AdmissionState state)
        {
            try { await Service.DecideAdmissionAsync(identity, state, default, new(session, 0)); return 200; }
            catch (GatewayFault error) { return error.Status; }
        }
        var results = await Task.WhenAll(Decide(first.Session, AdmissionState.Approved), Decide(second.Session, AdmissionState.Refused));
        Assert.Equal(new[] { 200, 409 }, results.Order());
        Assert.Equal(1, await database.ScalarLongAsync($"SELECT COUNT(*) FROM administrator_audit WHERE target = '{identity}'"));
        Assert.Equal(1, await database.ScalarLongAsync($"SELECT revision FROM admissions WHERE provider='github' AND subject='{identity.Subject}'"));
    }
}
