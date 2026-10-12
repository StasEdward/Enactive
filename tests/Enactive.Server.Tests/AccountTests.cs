namespace Enactive.Server.Tests;

using Enactive.Server.Accounts;

/// <summary>
/// The service's own accounts (ADR 0002 §4, server plan 1.3): passwords that are checked and never kept, a lockout a
/// guesser cannot afford, sessions that end when what their account may do changes, and an administration that starts
/// on the server itself.
/// </summary>
public sealed class AccountTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-accounts", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly UserStore _users;

    private const string Password = "correct horse battery";

    public AccountTests()
    {
        _users = new UserStore(_root, _clock);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ── the store ───────────────────────────────────────────────────────────

    [Fact]
    public void An_account_signs_in_with_its_password_and_is_found_by_any_case()
    {
        Assert.Null(_users.Add("alice", Password, Role.Operator));

        var (verdict, account) = _users.SignIn("ALICE", Password);

        Assert.Equal(SignInVerdict.Ok, verdict);
        Assert.Equal(("alice", Role.Operator), (account!.Name, account.Role));
    }

    /// <summary>The database holds a hash a stolen copy cannot sign in with - not the password.</summary>
    [Fact]
    public void The_password_is_not_in_the_database()
    {
        _users.Add("alice", Password, Role.Viewer);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(_root, "server.db")));
    }

    [Fact]
    public void A_wrong_password_and_an_unknown_name_are_the_same_answer()
    {
        _users.Add("alice", Password, Role.Viewer);

        Assert.Equal(SignInVerdict.Wrong, _users.SignIn("alice", "wrong password!").Verdict);
        Assert.Equal(SignInVerdict.Wrong, _users.SignIn("mallory", Password).Verdict);
    }

    [Fact]
    public void A_short_password_or_a_name_with_spaces_or_a_second_alice_is_refused()
    {
        Assert.Contains("at least", _users.Add("alice", "short", Role.Viewer));
        Assert.Contains("without spaces", _users.Add("al ice", Password, Role.Viewer));
        _users.Add("alice", Password, Role.Viewer);
        Assert.Contains("already", _users.Add("Alice", Password, Role.Admin));
    }

    /// <summary>
    /// Five wrong passwords and the account is refused for a quarter of an hour - the right password included, or the
    /// lockout would only slow a guesser to the speed of reading the answer.
    /// </summary>
    [Fact]
    public void Five_wrong_passwords_lock_the_account_even_against_the_right_one_until_the_time_is_up()
    {
        _users.Add("alice", Password, Role.Viewer);
        for (var i = 0; i < UserStore.FailuresBeforeLockout - 1; i++)
            Assert.Equal(SignInVerdict.Wrong, _users.SignIn("alice", "guess " + i + "xxxxxxxx").Verdict);

        Assert.Equal(SignInVerdict.Locked, _users.SignIn("alice", "the fifth guess!!").Verdict);
        Assert.Equal(SignInVerdict.Locked, _users.SignIn("alice", Password).Verdict);

        _clock.Now += UserStore.LockoutFor + TimeSpan.FromSeconds(1);
        Assert.Equal(SignInVerdict.Ok, _users.SignIn("alice", Password).Verdict);
    }

    /// <summary>
    /// A name that is no account locks as an account does. Only accounts could be locked, so five wrong passwords told a
    /// guesser whether a name was one: the page says "too many wrong passwords" for a lockout and nothing else.
    /// </summary>
    [Fact]
    public void A_name_that_is_no_account_locks_as_an_account_does()
    {
        for (var i = 0; i < UserStore.FailuresBeforeLockout - 1; i++)
            Assert.Equal(SignInVerdict.Wrong, _users.SignIn("nobody", "guess " + i + "xxxxxxxx").Verdict);

        Assert.Equal(SignInVerdict.Locked, _users.SignIn("NOBODY", "the fifth guess!!").Verdict);
        Assert.Equal(SignInVerdict.Locked, _users.SignIn("nobody", "and a sixth one!!").Verdict);

        _clock.Now += UserStore.LockoutFor + TimeSpan.FromSeconds(1);
        Assert.Equal(SignInVerdict.Wrong, _users.SignIn("nobody", "after the wait!!!").Verdict);
    }

    [Fact]
    public void A_right_password_clears_the_count_of_wrong_ones()
    {
        _users.Add("alice", Password, Role.Viewer);
        for (var i = 0; i < UserStore.FailuresBeforeLockout - 1; i++)
            _users.SignIn("alice", "wrong password " + i);

        _users.SignIn("alice", Password);

        Assert.Equal(SignInVerdict.Wrong, _users.SignIn("alice", "wrong once more!").Verdict);
    }

    [Fact]
    public void A_disabled_account_does_not_sign_in()
    {
        _users.Add("alice", Password, Role.Viewer);
        _users.SetDisabled("alice", true);

        Assert.Equal(SignInVerdict.Disabled, _users.SignIn("alice", Password).Verdict);
    }

    /// <summary>A new password, a new role, disabling: each changes the stamp, which is how open sessions learn of it.</summary>
    [Fact]
    public void Every_change_to_what_an_account_may_do_changes_its_stamp()
    {
        _users.Add("alice", Password, Role.Operator);
        var stamps = new List<string> { _users.Find("alice")!.Stamp };

        _users.SetPassword("alice", "another long password");
        stamps.Add(_users.Find("alice")!.Stamp);
        _users.SetRole("alice", Role.Viewer);
        stamps.Add(_users.Find("alice")!.Stamp);
        _users.SetDisabled("alice", true);
        stamps.Add(_users.Find("alice")!.Stamp);

        Assert.Equal(4, stamps.Distinct().Count());
    }

    [Fact]
    public void A_new_password_lifts_a_lockout()
    {
        _users.Add("alice", Password, Role.Viewer);
        for (var i = 0; i < UserStore.FailuresBeforeLockout; i++)
            _users.SignIn("alice", "wrong password " + i);

        _users.SetPassword("alice", "a brand new password");

        Assert.Equal(SignInVerdict.Ok, _users.SignIn("alice", "a brand new password").Verdict);
    }

    [Fact]
    public void The_audit_trail_keeps_what_was_done_by_whom_newest_first()
    {
        _users.Audit("alice", "signed in", null, "10.20.0.4");
        _users.Audit("alice", "run started", "tidy the wiki");

        var trail = _users.AuditTrail();

        Assert.Equal(["run started", "signed in"], trail.Select(e => e.Action));
        Assert.Equal("10.20.0.4", trail[1].Remote);
    }

    // ── on the server's own console ─────────────────────────────────────────

    private (int Code, string Out, string Err) Run(string[] args, params string[] typed)
    {
        var queue = new Queue<string>(typed);
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = AccountCommands.Run(args, _users, _ => queue.Dequeue(), output, errors);
        return (code, output.ToString(), errors.ToString());
    }

    /// <summary>The first account is the one that can make the others.</summary>
    [Fact]
    public void The_first_account_is_the_administrator()
    {
        Assert.Equal(65, Run(["--add-user", "alice", "--role", "Operator"], Password, Password).Code);

        var (code, said, _) = Run(["--add-user", "alice"], Password, Password);

        Assert.Equal(0, code);
        Assert.Contains("Admin", said);
        Assert.Equal(Role.Admin, _users.Find("alice")!.Role);
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "(console)", Action: "account added as Admin", Detail: "alice" });
    }

    [Fact]
    public void A_password_typed_differently_twice_changes_nothing()
    {
        var (code, _, err) = Run(["--add-user", "alice"], Password, Password + "!");

        Assert.Equal(65, code);
        Assert.Contains("did not match", err);
        Assert.Null(_users.Find("alice"));
    }

    /// <summary>A service whose only administrator is disabled or demoted cannot be administered from anywhere.</summary>
    [Fact]
    public void The_last_administrator_cannot_be_disabled_or_demoted()
    {
        _users.Add("alice", Password, Role.Admin);

        Assert.Equal(65, Run(["--disable-user", "alice"]).Code);
        Assert.Equal(65, Run(["--set-role", "alice", "Operator"]).Code);

        _users.Add("bob", Password, Role.Admin);
        Assert.Equal(0, Run(["--set-role", "alice", "Operator"]).Code);
        Assert.Equal(Role.Operator, _users.Find("alice")!.Role);
    }

    [Fact]
    public void The_accounts_are_listed_with_what_stops_them()
    {
        _users.Add("alice", Password, Role.Admin);
        _users.Add("bob", Password, Role.Viewer);
        _users.SetDisabled("bob", true);

        var (_, said, _) = Run(["--list-users"]);

        Assert.Contains("alice", said);
        Assert.Matches(@"bob\s+Viewer\s+disabled", said);
    }
}
