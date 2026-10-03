namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Gateway.Administration;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

public sealed class AdminStoreTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private Database Db => new(database.ConnectionString);
    private AdminStore Store => new(Db, TimeProvider.System);

    [Fact]
    public async Task Identity_is_case_sensitive_and_does_not_match_another_issuer()
    {
        var identity = Identity();
        await Store.GrantAsync(identity, default);
        Assert.True(AdminIdentity.TryCreate(identity.Issuer, identity.Subject.ToUpperInvariant(), out var otherCase));
        Assert.Null(await Store.OpenAsync(otherCase!, DateTimeOffset.UtcNow, default));
        Assert.True(AdminIdentity.TryCreate("https://another.example.test", identity.Subject, out var otherIssuer));
        Assert.Null(await Store.OpenAsync(otherIssuer!, DateTimeOffset.UtcNow, default));
        Assert.Null(await Store.OpenAsync(identity, DateTimeOffset.UtcNow.AddMinutes(-6), default));
    }

    [Fact]
    public async Task Failed_audit_rolls_back_revocation_and_session_creation()
    {
        var identity = Identity();
        await Store.GrantAsync(identity, default);
        var session = (await Store.OpenAsync(identity, DateTimeOffset.UtcNow, default))!;
        await database.ExecuteAsync("""
            CREATE TRIGGER reject_admin_security_audit BEFORE INSERT ON administrator_audit FOR EACH ROW
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected audit failure'
            """);
        try
        {
            await Assert.ThrowsAsync<MySqlException>(() => Store.RevokeAsync(identity, default));
            await Assert.ThrowsAsync<MySqlException>(() => Store.OpenAsync(identity, DateTimeOffset.UtcNow, default));
        }
        finally { await database.ExecuteAsync("DROP TRIGGER reject_admin_security_audit"); }
        Assert.NotNull(await Store.FindAsync(session.AdministratorId, session.SessionId, session.Version, default));
        Assert.Equal(1, await database.ScalarLongAsync($"SELECT COUNT(*) FROM administrator_sessions WHERE administrator_id = '{session.AdministratorId}'"));
    }

    [Fact]
    public async Task Scheduled_retention_removes_expired_sessions_and_old_admin_audit_only()
    {
        var identity = Identity();
        var id = await Store.GrantAsync(identity, default);
        var old = (await Store.OpenAsync(identity, DateTimeOffset.UtcNow, default))!;
        var current = (await Store.OpenAsync(identity, DateTimeOffset.UtcNow, default))!;
        await database.ExecuteAsync($"UPDATE administrator_sessions SET expires_at = UTC_TIMESTAMP(3) - INTERVAL 1 DAY WHERE id = '{old.SessionId}'");
        await database.ExecuteAsync($"UPDATE administrator_audit SET at = UTC_TIMESTAMP(3) - INTERVAL 91 DAY WHERE target = '{old.SessionId}'");
        await new Retention(Db, 30).TrimAsync();
        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM administrator_sessions WHERE id = '{old.SessionId}'"));
        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM administrator_audit WHERE target = '{old.SessionId}'"));
        Assert.NotNull(await Store.FindAsync(id, current.SessionId, current.Version, default));
        Assert.Equal(1, await database.ScalarLongAsync($"SELECT COUNT(*) FROM administrator_audit WHERE target = '{current.SessionId}'"));
    }

    private static AdminIdentity Identity()
    {
        Assert.True(AdminIdentity.TryCreate("https://issuer.example.test/tenant", "admin-" + Ids.New(), out var identity));
        return identity!;
    }
}
