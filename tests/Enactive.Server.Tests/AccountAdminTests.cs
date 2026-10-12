namespace Enactive.Server.Tests;

using System.Security.Claims;
using Enactive.Server.Accounts;

/// <summary>
/// The accounts, administered from the browser by an Admin (server plan phase 3) - with the console's rules, checked at
/// the action and audited.
/// </summary>
public sealed class AccountAdminTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-admin", Guid.NewGuid().ToString("N"));
    private readonly UserStore _users;
    private readonly AccountAdmin _admin;

    private const string Password = "correct horse battery";

    public AccountAdminTests()
    {
        _users = new UserStore(_root);
        _users.Add("ada", Password, Role.Admin);
        _users.Add("alice", Password, Role.Operator);
        _admin = new AccountAdmin(_users);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private ClaimsPrincipal As(string name) => ServerAuth.PrincipalOf(_users.Find(name)!);

    [Fact]
    public void An_admin_adds_an_account_that_then_signs_in_and_the_audit_says_who_added_it()
    {
        Assert.Equal(new AccountChange(true, "Added carl (Viewer)."), _admin.Do(As("ada"), AccountAdmin.Act.Add, " carl ", "Viewer", Password, Password));

        Assert.Equal(SignInVerdict.Ok, _users.SignIn("carl", Password).Verdict);
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "ada", Action: "account add", Detail: "carl (Viewer)" });
    }

    /// <summary>Checked at the action: an Operator's post to the accounts page changes nothing.</summary>
    [Fact]
    public void Nobody_but_an_admin_administers_accounts()
    {
        Assert.Contains("role does not include", _admin.Do(As("alice"), AccountAdmin.Act.Role, "alice", "Admin", null, null).Said);
        Assert.Equal("Your role does not include administering accounts.", _admin.Refusal(As("alice")));
        Assert.Null(_admin.Refusal(As("ada")));
        Assert.Equal(Role.Operator, _users.Find("alice")!.Role);
        Assert.Empty(_admin.List(As("alice")));
        Assert.Empty(_admin.Trail(As("alice")));
    }

    [Fact]
    public void A_password_typed_differently_or_too_short_changes_nothing()
    {
        Assert.Contains("did not match", _admin.Do(As("ada"), AccountAdmin.Act.Add, "carl", "Viewer", Password, Password + "!").Said);
        Assert.Contains("at least", _admin.Do(As("ada"), AccountAdmin.Act.Add, "carl", "Viewer", "short", "short").Said);
        Assert.Null(_users.Find("carl"));
    }

    /// <summary>The service must not be locked out of its own administration from its own page.</summary>
    [Fact]
    public void The_last_administrator_is_neither_disabled_nor_demoted_from_the_page()
    {
        Assert.Contains("last administrator", _admin.Do(As("ada"), AccountAdmin.Act.Disable, "ada", null, null, null).Said);
        Assert.Contains("last administrator", _admin.Do(As("ada"), AccountAdmin.Act.Role, "ada", "Operator", null, null).Said);
        Assert.Equal(Role.Admin, _users.Find("ada")!.Role);
    }

    [Fact]
    public void A_disabled_account_is_enabled_again_and_a_new_password_ends_its_sessions()
    {
        var before = _users.Find("alice")!.Stamp;

        _admin.Do(As("ada"), AccountAdmin.Act.Disable, "alice", null, null, null);
        Assert.True(_users.Find("alice")!.Disabled);
        _admin.Do(As("ada"), AccountAdmin.Act.Enable, "alice", null, null, null);
        Assert.Contains("sessions are ended", _admin.Do(As("ada"), AccountAdmin.Act.Password, "alice", null, "a brand new password", "a brand new password").Said);

        Assert.False(_users.Find("alice")!.Disabled);
        Assert.NotEqual(before, _users.Find("alice")!.Stamp);
        Assert.Equal(SignInVerdict.Ok, _users.SignIn("alice", "a brand new password").Verdict);
    }
}
