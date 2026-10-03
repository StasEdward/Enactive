namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>The same administrative operations must be usable without parsing a terminal's prose.</summary>
public sealed class AdministrationServiceTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private Database Db => new(database.ConnectionString);
    private AdministrationService Administration => new(Db, TimeProvider.System);

    [Fact]
    public async Task Waiting_identities_are_structured_and_decisions_reach_the_real_sign_in_path()
    {
        var identity = Identity();
        var accounts = new AccountService(Db, TimeProvider.System, AdmissionMode.List);
        const string display = "A name with <markup> and a newline\n";
        Assert.IsType<SignInOutcome.Waiting>(
            await accounts.SignInAsync(identity.Provider, identity.Subject, display, new SignInTicket(Ids.New() + Ids.New(), DateTimeOffset.UtcNow), default));

        var request = Assert.Single(await Administration.ListWaitingAsync(default), r => r.Identity == identity);
        Assert.Equal(display, request.Display);
        Assert.False((await Administration.DecideAdmissionAsync(identity, AdmissionState.Approved, default)).HasAccount);
        Assert.DoesNotContain(await Administration.ListWaitingAsync(default), r => r.Identity == identity);
        var signedIn = Assert.IsType<SignInOutcome.SignedIn>(
            await accounts.SignInAsync(identity.Provider, identity.Subject, display, new SignInTicket(Ids.New() + Ids.New(), DateTimeOffset.UtcNow), default));

        // Refusing registration after provisioning is not disabling an existing account. The web
        // adapter needs this flag to explain that distinction rather than invent its own query.
        Assert.True((await Administration.DecideAdmissionAsync(identity, AdmissionState.Refused, default)).HasAccount);
        var again = Assert.IsType<SignInOutcome.SignedIn>(
            await accounts.SignInAsync(identity.Provider, identity.Subject, display, new SignInTicket(Ids.New() + Ids.New(), DateTimeOffset.UtcNow), default));
        Assert.Equal(signedIn.Access.UserId, again.Access.UserId);
    }

    [Theory]
    [InlineData((int)AdmissionState.Waiting)]
    [InlineData(999)]
    public async Task A_non_cli_caller_cannot_write_an_invalid_admission_decision(int state)
    {
        var identity = Identity();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Administration.DecideAdmissionAsync(identity, (AdmissionState)state, default));
        Assert.Equal(0, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM admissions WHERE provider = '{identity.Provider}' AND subject = '{identity.Subject}'"));
    }

    [Fact]
    public async Task Missing_account_results_are_distinct_from_a_successful_change_with_no_queued_commands()
    {
        var missing = Ids.New();
        Assert.Null(await Administration.DisableAccountAsync(missing, default));
        Assert.False(await Administration.EnableAccountAsync(missing, default));
        Assert.False(await Administration.RevokeSessionsAsync(missing, default));
        Assert.Equal(0, await database.ScalarLongAsync($"SELECT COUNT(*) FROM audit WHERE target = '{missing}'"));

        var owner = await TestAccounts.CreateAsync(database, Ids.New());
        var disabled = Assert.IsType<AccountDisabledResult>(await Administration.DisableAccountAsync(owner.UserId, default));
        Assert.Equal(0, disabled.WithdrawnCommands);
        Assert.True(await Administration.EnableAccountAsync(owner.UserId, default));
        var sessions = new SessionStore(Db, TimeProvider.System);
        Assert.False(await sessions.ValidAsync(owner.UserId, owner.SessionId, 1, default));
        var fresh = await sessions.OpenAsync(owner.UserId, TestAccounts.Provider, default);
        Assert.True(await Administration.RevokeSessionsAsync(owner.UserId, default));
        Assert.Equal(0, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM user_sessions WHERE id = '{fresh.SessionId}' AND revoked_at IS NULL"));
        Assert.Equal(3, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM audit WHERE owner_id = '{owner.UserId}' AND actor = 'operator'"));
    }

    [Fact]
    public async Task Failed_audit_rolls_back_account_sessions_and_command_withdrawal_together()
    {
        var owner = await TestAccounts.CreateAsync(database, Ids.New());
        var users = new UserService(Db, Limits.Unlimited, TimeProvider.System);
        var host = await users.RegisterHostAsync(owner, "Test computer", default);
        var command = Ids.New();
        await database.ExecuteAsync("""
            INSERT INTO commands (owner_id, id, host_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES (@owner, @command, @host, 'CancelRun', '{}', SHA2(@command, 256), 'PendingDelivery',
                    UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL 1 DAY)
            """, ("@owner", owner.UserId), ("@command", command), ("@host", host.Id));

        // This class owns its database. Fail only the final write, after all account changes ran,
        // to catch a refactor that splits the audit off or commits any earlier write separately.
        await database.ExecuteAsync("""
            CREATE TRIGGER reject_administrative_audit BEFORE INSERT ON audit FOR EACH ROW
            BEGIN
              IF NEW.actor = 'operator' THEN
                SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected administrative audit failure';
              END IF;
            END
            """);
        try
        {
            await Assert.ThrowsAsync<MySqlException>(() => Administration.DisableAccountAsync(owner.UserId, default));
        }
        finally
        {
            await database.ExecuteAsync("DROP TRIGGER reject_administrative_audit");
        }

        Assert.Equal("Active", Assert.Single(await database.StringsAsync($"SELECT status FROM users WHERE id = '{owner.UserId}'")));
        Assert.True(await new SessionStore(Db, TimeProvider.System).ValidAsync(owner.UserId, owner.SessionId, 1, default));
        Assert.Equal("PendingDelivery", Assert.Single(await database.StringsAsync($"SELECT status FROM commands WHERE id = '{command}'")));
        Assert.Equal(0, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM audit WHERE owner_id = '{owner.UserId}' AND actor = 'operator'"));
    }

    private static AdmissionIdentity Identity()
    {
        Assert.True(AdmissionIdentity.TryParse("github:" + Ids.New(), out var identity));
        return identity!;
    }
}
