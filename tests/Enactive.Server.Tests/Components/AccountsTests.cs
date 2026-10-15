namespace Enactive.Server.Tests.Components;

using System.Globalization;
using System.Text.RegularExpressions;
using Bunit;
using Enactive.Server.Accounts;
using Enactive.Server.Components.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

/// <summary>
/// The accounts and the audit trail (web only, Admin), drawn as the settings' lists are: a card per account with its
/// pencil, Add under the list, each change in a dialog. Every change goes through AccountAdmin as the person signed in,
/// which checks the role at the change and audits it; the page only decides what to offer.
/// </summary>
public sealed partial class AccountsTests : ComponentHost
{
    private const string NewPassword = "a password long enough";

    private IRenderedComponent<AccountsScreen> Accounts(string who)
    {
        SignIn(who);
        var screen = Render<AccountsScreen>();
        screen.WaitForAssertion(() => Assert.True(screen.FindAll(".admin-screen h1").Count > 0));
        return screen;
    }

    private static AngleSharp.Dom.IElement Card(IRenderedComponent<AccountsScreen> screen, string name)
        => screen.FindAll(".settings-card").Single(c => c.QuerySelector(".card-title")!.TextContent.Trim() == name);

    private static AngleSharp.Dom.IElement Dialog(IRenderedComponent<AccountsScreen> screen) => screen.Find("[role='dialog']");

    private static void Press(AngleSharp.Dom.IElement scope, string button)
        => scope.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == button).Click();

    private static void Edit(IRenderedComponent<AccountsScreen> screen, string name)
        => screen.Find($"button[aria-label='Edit {name}']").Click();

    [Fact]
    public void An_admin_adds_an_account_and_it_signs_in()
    {
        var screen = Accounts("ada");

        Press(screen.Find(".admin-screen"), "Add");
        screen.Find("#account-name").Change(" carl ");
        screen.Find("#account-role").Change(nameof(Role.Viewer));
        screen.Find("#account-password").Input(NewPassword);
        screen.Find("#account-again").Input(NewPassword);
        Press(Dialog(screen), "Add");

        Assert.Equal("Added carl (Viewer).", screen.Find("[role='status']").TextContent.Trim());
        Assert.Empty(screen.FindAll("[role='dialog']"));
        Assert.Equal("Viewer · active", Card(screen, "carl").QuerySelector(".card-meta")!.TextContent.Trim());
        // The new account's name and password are the ones that sign in, and who added it is in the audit.
        Assert.Equal(SignInVerdict.Ok, Users.SignIn("carl", NewPassword).Verdict);
        Assert.Contains(Users.AuditTrail(), e => e is { Account: "ada", Action: "account add", Detail: "carl (Viewer)" });
        Assert.DoesNotContain(NewPassword, screen.Markup);
    }

    /// <summary>
    /// The service would be locked out of its own administration: the page offers Disable on every account, and the
    /// service refuses it for the last Admin - as it refuses a lower role for that one.
    /// </summary>
    [Fact]
    public void The_last_admin_cannot_be_disabled_from_the_page()
    {
        var screen = Accounts("ada");

        Edit(screen, "ada");
        Press(Dialog(screen), "Disable");

        Assert.Equal("That is the last administrator; make another one first.", Dialog(screen).QuerySelector("[role='alert']")!.TextContent.Trim());
        Assert.False(Users.Find("ada")!.Disabled);

        Dialog(screen).QuerySelector("#account-role")!.Change(nameof(Role.Operator));
        Press(Dialog(screen), "Set role");

        Assert.Equal("That is the last administrator; make another one first.", Dialog(screen).QuerySelector("[role='alert']")!.TextContent.Trim());
        Assert.Equal(Role.Admin, Users.Find("ada")!.Role);
        Assert.DoesNotContain(Users.AuditTrail(), e => e.Action.StartsWith("account ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The address is an Admin's (the router refuses it to anybody else, as it refused /admin/users), and the service
    /// shows the accounts to nobody else, whatever page asks.
    /// </summary>
    [Theory]
    [InlineData("alice")]
    [InlineData("vera")]
    public void An_operator_is_refused_the_accounts_page(string who)
    {
        Assert.Contains(typeof(AccountsScreen).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false),
            a => ((AuthorizeAttribute)a).Policy == "Admin");

        var screen = Accounts(who);

        Assert.Equal("Your role does not include administering accounts.", screen.Find("[role='alert']").TextContent.Trim());
        Assert.Empty(screen.FindAll(".settings-card"));
        Assert.Empty(screen.FindAll("button"));
    }

    /// <summary>ADR 0002's trust model, said where a role is given, because it is where somebody would assume otherwise.</summary>
    [Fact]
    public void The_trust_model_is_said_where_roles_are_given()
    {
        var screen = Accounts("ada");

        // As read: the markup wraps the sentence over lines.
        var warning = Regex.Replace(screen.Find(".admin-trust").TextContent, @"\s+", " ");
        Assert.Contains("The people signed in to this service trust each other.", warning);
        Assert.Contains("Give Operator and Admin to people you would give a login to this machine.", warning);
    }

    /// <summary>
    /// A password typed on the page reaches the service and nothing else: the boxes are never drawn with it - not while
    /// it is being typed, not after a refusal - and they are empty again after every try, made or refused.
    /// </summary>
    [Fact]
    public void A_password_is_never_drawn_back_and_the_boxes_are_empty_after_every_try()
    {
        var screen = Accounts("ada");
        Edit(screen, "alice");
        Assert.All(Dialog(screen).QuerySelectorAll("input[type='password']"),
            box => Assert.Equal("new-password", box.GetAttribute("autocomplete")));

        screen.Find("#account-password").Input(NewPassword);
        screen.Find("#account-again").Input(NewPassword + "!");
        // Something else on the page drawn again while the boxes hold what was typed.
        Dialog(screen).QuerySelector("#account-role")!.Change(nameof(Role.Viewer));
        Assert.DoesNotContain(NewPassword, screen.Markup);

        Press(Dialog(screen), "Set password");

        Assert.Equal("The two passwords did not match; nothing was changed.", Dialog(screen).QuerySelector("[role='alert']")!.TextContent.Trim());
        Assert.DoesNotContain(NewPassword, screen.Markup);
        // Empty again: typing only the second box now is a mismatch, not the first box's old text matched.
        screen.Find("#account-again").Input(NewPassword);
        Press(Dialog(screen), "Set password");
        Assert.Equal("The two passwords did not match; nothing was changed.", Dialog(screen).QuerySelector("[role='alert']")!.TextContent.Trim());

        var stamp = Users.Find("alice")!.Stamp;
        screen.Find("#account-password").Input(NewPassword);
        screen.Find("#account-again").Input(NewPassword);
        Press(Dialog(screen), "Set password");

        Assert.Equal("New password set for alice; its sessions are ended.", screen.Find("[role='status']").TextContent.Trim());
        Assert.Equal(SignInVerdict.Ok, Users.SignIn("alice", NewPassword).Verdict);
        Assert.NotEqual(stamp, Users.Find("alice")!.Stamp);
        Assert.DoesNotContain(NewPassword, screen.Markup);
    }

    /// <summary>A short password is refused as the console refuses it, and the boxes are empty again for the next try.</summary>
    [Fact]
    public void A_short_password_is_refused_and_the_account_is_not_added()
    {
        var screen = Accounts("ada");

        Press(screen.Find(".admin-screen"), "Add");
        screen.Find("#account-name").Change("carl");
        screen.Find("#account-password").Input("short");
        screen.Find("#account-again").Input("short");
        Press(Dialog(screen), "Add");

        Assert.Equal($"A password is at least {UserStore.MinimumPasswordLength} characters.",
            Dialog(screen).QuerySelector("[role='alert']")!.TextContent.Trim());
        Assert.Null(Users.Find("carl"));
        // The name and the role stay for another try; the password is typed again.
        Assert.Equal("carl", screen.Find("#account-name").GetAttribute("value"));
        Assert.All(Dialog(screen).QuerySelectorAll("input[type='password']"), box => Assert.True(string.IsNullOrEmpty(box.GetAttribute("value"))));
    }

    /// <summary>A role changed and an account disabled from the page end that person's sessions, and each is audited.</summary>
    [Fact]
    public void A_new_role_and_a_disable_end_that_person_s_sessions()
    {
        var screen = Accounts("ada");
        var stamp = Users.Find("alice")!.Stamp;

        Edit(screen, "alice");
        Dialog(screen).QuerySelector("#account-role")!.Change(nameof(Role.Viewer));
        Press(Dialog(screen), "Set role");

        Assert.Equal("alice is now Viewer; its sessions are ended.", screen.Find("[role='status']").TextContent.Trim());
        Assert.Equal("Viewer · active", Card(screen, "alice").QuerySelector(".card-meta")!.TextContent.Trim());
        Assert.NotEqual(stamp, Users.Find("alice")!.Stamp);

        Edit(screen, "alice");
        Press(Dialog(screen), "Disable");

        Assert.Equal("alice is disabled; its sessions are ended.", screen.Find("[role='status']").TextContent.Trim());
        Assert.Contains("tone-faint", Card(screen, "alice").ClassList);
        Assert.Contains(Users.AuditTrail(), e => e is { Account: "ada", Action: "account role", Detail: "alice (Viewer)" });
        Assert.Contains(Users.AuditTrail(), e => e is { Account: "ada", Action: "account disable", Detail: "alice" });

        Edit(screen, "alice");
        Press(Dialog(screen), "Enable");
        Assert.False(Users.Find("alice")!.Disabled);
    }

    /// <summary>
    /// Checked at every change, not when the page opened: an Admin whose session no longer speaks for them changes
    /// nothing from a page that is still open.
    /// </summary>
    [Fact]
    public void A_page_whose_admin_was_changed_since_it_opened_changes_nothing()
    {
        var screen = Accounts("ada");
        Users.SetPassword("ada", "somebody else's new password");

        Edit(screen, "alice");
        Press(Dialog(screen), "Disable");

        Assert.Equal("Your session has ended. Sign in again.", Dialog(screen).QuerySelector("[role='alert']")!.TextContent.Trim());
        Assert.False(Users.Find("alice")!.Disabled);
    }

    /// <summary>
    /// An Admin who sets their own password ends their own session with it: the page then refuses them, and still says
    /// what was done - the refusal alone read as if the change had not been made.
    /// </summary>
    [Fact]
    public void An_admin_s_own_change_is_still_said_when_the_page_then_refuses_them()
    {
        var screen = Accounts("ada");

        Edit(screen, "ada");
        screen.Find("#account-password").Input(NewPassword);
        screen.Find("#account-again").Input(NewPassword);
        Press(Dialog(screen), "Set password");

        Assert.Equal("New password set for ada; its sessions are ended.", screen.Find("[role='status']").TextContent.Trim());
        Assert.Equal("Your session has ended. Sign in again.", screen.Find("[role='alert']").TextContent.Trim());
        Assert.Empty(screen.FindAll(".settings-card"));
    }

    /// <summary>
    /// Closed until the role is checked: while the person is still being read the page draws nothing of an Admin's - it
    /// drew the trust warning and Add, a null refusal taken for "allowed".
    /// </summary>
    [Fact]
    public void Nothing_of_an_admin_s_is_drawn_before_the_role_is_checked()
    {
        var who = new TaskCompletionSource<AuthenticationState>();
        var accounts = Render<AccountsScreen>(p => p.AddCascadingValue(who.Task));
        var audit = Render<AuditScreen>(p => p.AddCascadingValue(who.Task));

        Assert.Empty(accounts.FindAll(".admin-trust, button, [role='alert'], [role='status']"));
        Assert.Empty(audit.FindAll("table, [role='alert'], .hint"));

        who.SetResult(new AuthenticationState(ServerAuth.PrincipalOf(Users.Find("ada")!)));
        accounts.WaitForAssertion(() => Assert.NotEmpty(accounts.FindAll(".admin-trust")));
        audit.WaitForAssertion(() => Assert.NotEmpty(audit.FindAll(".hint")));
    }

    /// <summary>A lockout says until when in the same figures on any machine: the current culture made it "14.05" on some.</summary>
    [Fact]
    public void A_locked_account_says_until_when_whatever_the_culture()
    {
        for (var i = 0; i < UserStore.FailuresBeforeLockout; i++)
            Users.SignIn("alice", "not the password");
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
        try
        {
            var screen = Accounts("ada");

            var card = Card(screen, "alice");
            Assert.Matches(@"^Operator · locked until \d\d:\d\d$", card.QuerySelector(".card-meta")!.TextContent.Trim());
            Assert.Contains("tone-warning", card.ClassList);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    // ── the audit trail ─────────────────────────────────────────────────────

    private IRenderedComponent<AuditScreen> Audit(string who)
    {
        SignIn(who);
        var screen = Render<AuditScreen>();
        screen.WaitForAssertion(() => Assert.True(screen.FindAll(".admin-screen h1").Count > 0));
        return screen;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$")]
    private static partial Regex InvariantTime();

    /// <summary>Newest first, the old page's columns, its times in the same figures whatever the machine's culture.</summary>
    [Fact]
    public void The_audit_is_newest_first_in_the_old_columns_and_invariant_times()
    {
        Users.Audit("alice", "signed in", null, "10.20.0.4");
        Users.Audit("alice", "run started", "tidy the wiki");
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
        try
        {
            var screen = Audit("ada");

            Assert.Equal(["When", "Account", "Action", "Detail", "From"], screen.FindAll("thead th").Select(h => h.TextContent.Trim()));
            var rows = screen.FindAll("tbody tr").Select(r => r.QuerySelectorAll("td").Select(c => c.TextContent.Trim()).ToArray()).ToList();
            Assert.Equal(["alice", "run started", "tidy the wiki", ""], rows[0][1..]);
            Assert.Equal(["alice", "signed in", "", "10.20.0.4"], rows[1][1..]);
            Assert.All(rows, r => Assert.Matches(InvariantTime(), r[0]));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("vera")]
    public void Only_an_admin_reads_the_audit(string who)
    {
        Users.Audit("alice", "signed in", null, "10.20.0.4");
        Assert.Contains(typeof(AuditScreen).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false),
            a => ((AuthorizeAttribute)a).Policy == "Admin");

        var screen = Audit(who);

        Assert.Equal("Your role does not include administering accounts.", screen.Find("[role='alert']").TextContent.Trim());
        Assert.Empty(screen.FindAll("table"));
    }

    /// <summary>Exactly as many as are drawn: the page says nothing of older entries, because there are none.</summary>
    [Fact]
    public void At_exactly_the_limit_the_audit_says_nothing_of_older_entries()
    {
        for (var i = 0; i < AuditScreen.Newest; i++)
            Users.Audit("alice", "run started", $"task {i}");

        var screen = Audit("ada");

        Assert.Equal(AuditScreen.Newest, screen.FindAll("tbody tr").Count);
        Assert.DoesNotContain("The newest", screen.Find(".admin-screen").TextContent);
    }

    /// <summary>The newest entries only, and the page says so: the table is never trimmed, and years of sign-ins drawn at once would be the page.</summary>
    [Fact]
    public void The_audit_shows_the_newest_entries_and_says_when_there_are_more()
    {
        for (var i = 0; i < AuditScreen.Newest + 1; i++)
            Users.Audit("alice", "run started", $"task {i}");

        var screen = Audit("ada");

        Assert.Equal(AuditScreen.Newest, screen.FindAll("tbody tr").Count);
        Assert.Equal($"task {AuditScreen.Newest}", screen.Find("tbody tr td:nth-child(4)").TextContent.Trim());
        Assert.Contains($"The newest {AuditScreen.Newest} entries", screen.Find(".admin-screen").TextContent);
    }
}
