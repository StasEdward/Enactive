namespace Enactive.Engine.Tests;

using Enactive.Core.Mail;
using Enactive.Core.Permissions;
using Enactive.Tools;
using MailKit.Security;
using Xunit;

/// <summary>
/// The first tool that acts outside this machine, and the rules that follow from that.
///
/// <para>Every other tool changes a workspace, and a workspace can be put back: the journal keeps
/// what was there, a rejected step restores it, and the worst case is work to redo. A sent
/// message cannot be recalled and what it carried has left.</para>
///
/// <para>The work it exists for is reading somebody's log off somebody's server. Text a model
/// READS is not an instruction — but a mail tool that sends wherever it is told makes that a
/// promise rather than a boundary, and under <c>--approve allow</c> nobody is watching. So the
/// recipient comes from settings and the tool refuses anything else by comparison, never by
/// judgement.</para>
/// </summary>
public sealed class SendEmailTests
{
    private static MailAccount Account(params string[] recipients)
        => new("smtp.test", 587, true, "me@test", "secret", "me@test", recipients);

    // ── the boundary ────────────────────────────────────────────────────────

    /// <summary>
    /// THE ONE THAT MATTERS. An address nobody listed is refused before anything is connected to,
    /// and the refusal names the list so a model that guessed once does not guess again.
    /// </summary>
    [Fact]
    public async Task An_address_nobody_listed_is_refused()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(
            new SendEmailTool(Account("me@test")),
            """{"subject":"hi","to":"someone.else@elsewhere.example"}""");

        Assert.False(result.Success);
        Assert.Contains("me@test", result.Error ?? "", StringComparison.Ordinal);

        // And it says who decides, because the model's next move should be to report, not retry.
        Assert.Contains("Settings", result.Error ?? "", StringComparison.Ordinal);
    }

    /// <summary>Case and stray spaces are spellings of an address, not different addresses.</summary>
    [Theory]
    [InlineData("ME@TEST")]
    [InlineData("  me@test  ")]
    public void A_listed_address_is_recognised_however_it_is_written(string written)
        => Assert.True(Account("me@test").Allows(written));

    /// <summary>
    /// It always asks, whatever the policy says — like the shells, and for a stronger reason:
    /// a shell command can be undone and this cannot.
    /// </summary>
    [Fact]
    public void It_asks_every_time()
    {
        var tool = new SendEmailTool(Account("me@test"));

        Assert.True(tool.RequiresApproval);
        Assert.Equal(PermissionLevel.Execute, tool.RequiredLevel);
    }

    // ── the approval question, and turning it off ─────────────────────────────

    /// <summary>
    /// It asks by default, whatever the policy says. That is the feature: the question names the
    /// recipient, the subject and every attachment, and a person sees what is about to leave.
    /// </summary>
    [Fact]
    public void By_default_every_send_asks_first()
        => Assert.True(new SendEmailTool(Account("me@test")).RequiresApproval);

    /// <summary>
    /// And a person can answer once, in settings, instead of every time.
    ///
    /// <para>Why that had to exist: a run nobody is watching is never OFFERED a tool whose only
    /// outcome is a prompt ("needs an approval nobody is there to give"), so with the question
    /// always on, a scheduled task could not mail its own report — which is the errand this tool
    /// was built for. The consent is then carried entirely by the recipient list, which is still a
    /// list a person typed and no task can add to; the test below is the half that proves the
    /// switch did not also open the boundary.</para>
    /// </summary>
    [Fact]
    public void A_person_can_answer_once_instead_of_every_time()
    {
        var standing = Account("me@test") with { SendWithoutAsking = true };

        Assert.False(new SendEmailTool(standing).RequiresApproval);
    }

    /// <summary>
    /// The switch turns off the QUESTION and nothing else. An address nobody listed is refused
    /// exactly as before — the one thing that must not follow from "do not ask me again".
    /// </summary>
    [Fact]
    public async Task Sending_without_asking_does_not_widen_who_may_be_written_to()
    {
        using var fx = new EngineFixture();
        var standing = Account("me@test") with { SendWithoutAsking = true };

        var result = await fx.Invoke(
            new SendEmailTool(standing),
            """{"subject":"hi","to":"someone.else@elsewhere.example"}""");

        Assert.False(result.Success);
        Assert.Contains("me@test", result.Error ?? "", StringComparison.Ordinal);
    }

    // ── what the STARTTLS switch is worth ───────────────────────────────────

    /// <summary>
    /// THE ONE THAT SENT NOTHING. Off is off: the Settings pane's own words are implicit TLS on
    /// 465 and no TLS otherwise, and the tool passed <c>Auto</c>, which upgrades whenever the
    /// server merely advertises STARTTLS. A relay with a certificate issued to its own LAN name -
    /// the shape of every internal relay - then fails on the hostname alone, and no setting the
    /// person can see accounts for it.
    /// </summary>
    [Fact]
    public void StartTls_off_upgrades_for_nothing_but_implicit_tls()
    {
        var plain = Account("me@test") with { StartTls = false, Port = 25 };

        Assert.Equal(SecureSocketOptions.None, SendEmailTool.SocketOptions(plain));
        Assert.Equal(SecureSocketOptions.SslOnConnect,
            SendEmailTool.SocketOptions(plain with { Port = 465 }));
    }

    /// <summary>On, the other way, still REQUIRES the upgrade rather than hoping for one.</summary>
    [Fact]
    public void StartTls_on_requires_the_upgrade()
        => Assert.Equal(SecureSocketOptions.StartTls, SendEmailTool.SocketOptions(Account("me@test")));

    // ── nothing configured ──────────────────────────────────────────────────

    /// <summary>
    /// With no account it is still REGISTERED - a role names it, and the set the roles name and
    /// the set the hosts register have to be the same set - so it says so in its description
    /// instead, where learning it costs no call.
    /// </summary>
    [Fact]
    public void With_no_account_the_description_says_so()
    {
        var text = new SendEmailTool(MailAccount.None).Definition.Description;

        Assert.Contains("NOT AVAILABLE", text, StringComparison.Ordinal);
        Assert.Contains("no SMTP account", text, StringComparison.Ordinal);
    }

    /// <summary>And a call made anyway is refused in as many words, pointing at the fix.</summary>
    [Fact]
    public async Task With_no_account_a_call_is_refused_and_says_where_to_fix_it()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new SendEmailTool(MailAccount.None), """{"subject":"hi"}""");

        Assert.False(result.Success);
        Assert.Contains("SMTP", result.Error ?? "", StringComparison.Ordinal);
    }

    /// <summary>A configured account names its recipients up front, so they need not be guessed.</summary>
    [Fact]
    public void A_configured_account_names_its_recipients_in_the_description()
        => Assert.Contains("me@test",
            new SendEmailTool(Account("me@test")).Definition.Description, StringComparison.Ordinal);

    // ── what it carries ─────────────────────────────────────────────────────

    /// <summary>
    /// An attachment that is not there is said plainly, and BEFORE anything is sent: half a
    /// message with the interesting file missing is worse than none.
    /// </summary>
    [Fact]
    public async Task A_missing_attachment_stops_the_send()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(
            new SendEmailTool(Account("me@test")),
            """{"subject":"report","attachments":["nowhere.md"]}""");

        Assert.False(result.Success);
        Assert.Contains("nowhere.md", result.Error ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// And one outside the workspace is refused by the same authority as every other path: a mail
    /// tool would otherwise be a way to read a file the file tools will not open.
    /// </summary>
    [Fact]
    public async Task An_attachment_outside_the_workspace_is_refused()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(
            new SendEmailTool(Account("me@test")),
            """{"subject":"report","attachments":["../../secrets.txt"]}""");

        Assert.False(result.Success);
    }

    /// <summary>A subject is the one thing a message cannot do without.</summary>
    [Fact]
    public async Task Without_a_subject_nothing_is_attempted()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new SendEmailTool(Account("me@test")), """{"body":"hello"}""");

        Assert.True(result.DidNotRun, "a sentence that did not parse, not a send that failed");
    }

    // ── the account itself ──────────────────────────────────────────────────

    [Fact]
    public void An_account_with_no_recipients_is_not_configured()
        => Assert.False(new MailAccount("smtp.test", 587, true, "u", "p", "f", Array.Empty<string>()).Configured);

    [Fact]
    public void An_account_with_no_host_is_not_configured()
        => Assert.False(Account("me@test") with { Host = "" } is { Configured: true });

    /// <summary>The From falls back to the user, which is what nearly every account wants.</summary>
    [Fact]
    public void A_blank_from_sends_as_the_user()
        => Assert.Equal("me@test", (Account("me@test") with { From = "" }).Sender);
}
