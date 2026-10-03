namespace Enactive.Remote.Gateway.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// How an account comes to exist on a public gateway: only for an identity the operator has admitted,
/// keyed by (provider, subject) and nothing else, and how the operator ends one.
///
/// <para>The operator's half is driven through <see cref="AdminCommands"/>, the same code the
/// <c>admin</c> command line runs, so what a test sets up is what an operator would have typed.</para>
/// </summary>
public sealed class AccountTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private Database Db => new(database.ConnectionString);

    private AccountService Accounts(AdmissionMode mode = AdmissionMode.List)
        => new(Db, TimeProvider.System, mode);

    private SessionStore Sessions => new(Db, TimeProvider.System);

    /// <summary>A provider's answer never redeemed, issued now: what each sign-in through a provider carries.</summary>
    private static SignInTicket Fresh() => new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow);

    /// <summary>
    /// A fresh answer issued a millisecond after the account's sessions were last ended, as stored. One issued
    /// "now" right after a revocation can fall in the revocation's own millisecond, and is then refused as
    /// issued before it - a test that failed now and then for no reason it was about.
    /// </summary>
    private async Task<SignInTicket> AfterRevocationAsync(string userId)
    {
        var revoked = (DateTime)(await database.ScalarAsync(
            $"SELECT sessions_revoked_at FROM users WHERE id = '{userId}'"))!;
        return Fresh() with { Issued = new DateTimeOffset(DateTime.SpecifyKind(revoked, DateTimeKind.Utc)).AddMilliseconds(1) };
    }

    private static string Subject(string stem = "u") => stem + "-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Runs one operator command; returns its exit code and what it printed.</summary>
    private async Task<(int Exit, string Output)> OperatorAsync(params string[] args)
    {
        var output = new StringWriter();
        var exit = await AdminCommands.RunAsync(args, Db, output);
        return (exit, output.ToString());
    }

    private async Task ApproveAsync(string provider, string subject)
        => Assert.Equal(0, (await OperatorAsync("approve", $"{provider}:{subject}")).Exit);

    /// <summary>An admitted, signed-in person: the account and the session its sign-in opened.</summary>
    private async Task<SignInOutcome.SignedIn> AdmittedAsync(string provider, string subject, string display = "Person")
    {
        await ApproveAsync(provider, subject);
        return Assert.IsType<SignInOutcome.SignedIn>(
            await Accounts().SignInAsync(provider, subject, display, Fresh(), default));
    }

    private Task<long> CountAsync(string sql) => database.ScalarLongAsync(sql);

    // ── admission ───────────────────────────────────────────────────────────

    /// <summary>
    /// The default is the list: an identity nobody has admitted gets no account and no session, and the
    /// operator can see it is asking. Asking again changes nothing but what it is called.
    /// </summary>
    [Fact]
    public async Task An_unadmitted_identity_waits_and_gets_no_session()
    {
        var subject = Subject();

        Assert.IsType<SignInOutcome.Waiting>(await Accounts().SignInAsync("github", subject, "octocat", Fresh(), default));
        Assert.IsType<SignInOutcome.Waiting>(await Accounts().SignInAsync("github", subject, "octocat-renamed", Fresh(), default));

        Assert.Equal(0, await CountAsync(
            $"SELECT COUNT(*) FROM external_identities WHERE subject = '{subject}'"));
        Assert.Equal(("Waiting", "octocat-renamed"), (
            Assert.Single(await database.StringsAsync(
                $"SELECT state FROM admissions WHERE provider = 'github' AND subject = '{subject}'")),
            Assert.Single(await database.StringsAsync(
                $"SELECT display FROM admissions WHERE provider = 'github' AND subject = '{subject}'"))));
        Assert.Contains(subject, (await OperatorAsync("admissions")).Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Admitted once, an account once: signing in twice is the same person with two sessions, not two
    /// people.
    /// </summary>
    [Fact]
    public async Task An_approved_identity_gets_an_account_once()
    {
        var subject = Subject();
        var first = await AdmittedAsync("github", subject, "octocat");
        var second = Assert.IsType<SignInOutcome.SignedIn>(
            await Accounts().SignInAsync("github", subject, "octocat", Fresh(), default));

        Assert.Equal(first.Access.UserId, second.Access.UserId);
        Assert.NotEqual(first.Access.SessionId, second.Access.SessionId);
        Assert.Equal(1, await CountAsync(
            $"SELECT COUNT(*) FROM external_identities WHERE subject = '{subject}'"));
        Assert.Equal(2, await CountAsync(
            $"SELECT COUNT(*) FROM user_sessions WHERE user_id = '{first.Access.UserId}'"));
        Assert.True(await Sessions.ValidAsync(
            first.Access.UserId, first.Access.SessionId, first.SecurityVersion, default));
    }

    /// <summary>
    /// The identity is the provider's numeric id, not the login: a person who renames their GitHub
    /// account is still the same person, and the new name is only what they are shown under.
    /// </summary>
    [Fact]
    public async Task A_changed_github_login_keeps_the_account()
    {
        var subject = Subject();
        var before = await AdmittedAsync("github", subject, "old-login");
        var after = Assert.IsType<SignInOutcome.SignedIn>(
            await Accounts().SignInAsync("github", subject, "new-login", Fresh(), default));

        Assert.Equal(before.Access.UserId, after.Access.UserId);
        Assert.Equal("new-login", Assert.Single(await database.StringsAsync(
            $"SELECT display FROM external_identities WHERE provider = 'github' AND subject = '{subject}'")));
        Assert.Equal(1, await CountAsync(
            $"SELECT COUNT(*) FROM users WHERE id = '{before.Access.UserId}'"));
    }

    /// <summary>
    /// An email is a claim a provider makes about itself, and two providers can both claim one address.
    /// Matching it would hand one person's account to whoever holds the address on another service.
    /// </summary>
    [Fact]
    public async Task Two_identities_with_the_same_email_are_two_accounts()
    {
        var github = await AdmittedAsync("github", Subject("gh"), "shared@example.com");
        var google = await AdmittedAsync("google", Subject("goog"), "shared@example.com");

        Assert.NotEqual(github.Access.UserId, google.Access.UserId);
    }

    /// <summary>
    /// A refused identity is told so and gets nothing. An operator's refusal stands in open mode too:
    /// "open" admits whoever has not been decided about, not whoever was turned away.
    /// </summary>
    [Fact]
    public async Task A_refused_identity_gets_no_account_even_when_admission_is_open()
    {
        var subject = Subject();
        Assert.Equal(0, (await OperatorAsync("refuse", $"github:{subject}")).Exit);

        Assert.IsType<SignInOutcome.Refused>(await Accounts().SignInAsync("github", subject, "x", Fresh(), default));
        Assert.IsType<SignInOutcome.Refused>(
            await Accounts(AdmissionMode.Open).SignInAsync("github", subject, "x", Fresh(), default));
        Assert.Equal(0, await CountAsync(
            $"SELECT COUNT(*) FROM external_identities WHERE subject = '{subject}'"));
    }

    /// <summary>
    /// The list decides who may CREATE an account. Whoever has one signs in whatever the list now says:
    /// they were admitted once, and taking an account away is what disabling it is for.
    /// </summary>
    [Fact]
    public async Task An_existing_account_signs_in_whatever_the_admission_row_says()
    {
        var subject = Subject();
        var first = await AdmittedAsync("github", subject);

        var refused = await OperatorAsync("refuse", $"github:{subject}");
        Assert.Equal(0, refused.Exit);
        Assert.Contains("already has an account", refused.Output, StringComparison.Ordinal);

        var again = Assert.IsType<SignInOutcome.SignedIn>(
            await Accounts().SignInAsync("github", subject, "Person", Fresh(), default));
        Assert.Equal(first.Access.UserId, again.Access.UserId);
    }

    /// <summary>The operator may admit somebody who has not tried yet; they then sign straight in.</summary>
    [Fact]
    public async Task An_operator_can_approve_an_identity_that_has_not_asked()
    {
        var subject = Subject();

        await ApproveAsync("google", subject);

        Assert.Equal("Approved", Assert.Single(await database.StringsAsync(
            $"SELECT state FROM admissions WHERE provider = 'google' AND subject = '{subject}'")));
        Assert.IsType<SignInOutcome.SignedIn>(
            await Accounts().SignInAsync("google", subject, "Newcomer", Fresh(), default));
        Assert.Equal("Newcomer", Assert.Single(await database.StringsAsync(
            $"SELECT display FROM admissions WHERE provider = 'google' AND subject = '{subject}'")));
    }

    /// <summary>
    /// Open admission admits on first sight, and one that was already waiting - it asked while the list
    /// was in force - is admitted too, not stranded behind a decision nobody made.
    /// </summary>
    [Fact]
    public async Task Open_admission_admits_on_first_sign_in()
    {
        var waiting = Subject("waiting");
        var fresh = Subject("fresh");
        Assert.IsType<SignInOutcome.Waiting>(await Accounts().SignInAsync("github", waiting, "w", Fresh(), default));

        Assert.IsType<SignInOutcome.SignedIn>(
            await Accounts(AdmissionMode.Open).SignInAsync("github", fresh, "f", Fresh(), default));
        Assert.IsType<SignInOutcome.SignedIn>(
            await Accounts(AdmissionMode.Open).SignInAsync("github", waiting, "w", Fresh(), default));
    }

    /// <summary>
    /// Open admission is only safe behind quotas: without them one stranger's script fills the
    /// database for everybody. The gateway reads real limits from configuration, so this registers the
    /// unlimited ones in their place; with those it will not start in that mode. Shown red by removing
    /// the check, which leaves a gateway that starts and admits the world.
    /// </summary>
    [Fact]
    public async Task Open_admission_refuses_to_start_without_limits()
    {
        await using var gateway = TestGateway.Create(database, configure: builder =>
        {
            builder.UseSetting(Admission.Setting, "open");
            builder.ConfigureTestServices(services => services.AddSingleton(Limits.Unlimited));
        });

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => gateway.CreateClient().GetAsync("/health"));

        Assert.Contains(Admission.Setting, refused.Message, StringComparison.Ordinal);
        Assert.Contains("limits", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And with limits registered it starts, so the check is about the limits and not the word.</summary>
    [Fact]
    public async Task Open_admission_starts_when_limits_are_set()
    {
        await using var gateway = TestGateway.Create(database, configure: builder =>
        {
            builder.UseSetting(Admission.Setting, "open");
            builder.ConfigureTestServices(services =>
                services.AddSingleton(Limits.Unlimited with { HostsPerUser = 5 }));
        });

        using var response = await gateway.CreateClient().GetAsync("/health");

        Assert.True(response.IsSuccessStatusCode);
    }

    /// <summary>A typo in a security switch is not read as the default.</summary>
    [Fact]
    public async Task An_unknown_admission_mode_refuses_to_start()
    {
        await using var gateway = TestGateway.Create(database,
            configure: builder => builder.UseSetting(Admission.Setting, "everyone"));

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => gateway.CreateClient().GetAsync("/health"));

        Assert.Contains("everyone", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The development sign-in is not behind admission: it is Development-only and exists so a developer
    /// has an account without an operator in the loop.
    /// </summary>
    [Fact]
    public async Task Development_sign_in_still_works_with_the_list_in_force()
    {
        await using var gateway = TestGateway.Create(database);
        using var browser = await PanelClient.SignedInAsync(gateway, Subject("dev"));

        Assert.NotEmpty(browser.UserId);
    }

    // ── disabling ───────────────────────────────────────────────────────────

    /// <summary>
    /// A disabled person is told so, rather than waiting or being refused: the answer tells the panel
    /// what to say. Enabling restores the sign-in with the same account.
    /// </summary>
    [Fact]
    public async Task A_disabled_account_cannot_sign_in()
    {
        var subject = Subject();
        var account = await AdmittedAsync("github", subject);

        Assert.Equal(0, (await OperatorAsync("disable", account.Access.UserId)).Exit);
        Assert.IsType<SignInOutcome.Disabled>(await Accounts().SignInAsync("github", subject, "Person", Fresh(), default));

        Assert.Equal(0, (await OperatorAsync("enable", account.Access.UserId)).Exit);
        var back = Assert.IsType<SignInOutcome.SignedIn>(
            await Accounts().SignInAsync("github", subject, "Person", await AfterRevocationAsync(account.Access.UserId), default));
        Assert.Equal(account.Access.UserId, back.Access.UserId);
    }

    /// <summary>
    /// Disabling ends every session at once and moves the security version on, and touches nobody
    /// else. Enabling afterwards does not bring the old sessions back.
    /// </summary>
    [Fact]
    public async Task Disabling_revokes_every_session_and_only_that_accounts()
    {
        var subject = Subject();
        var mine = await AdmittedAsync("github", subject);
        var other = await AdmittedAsync("github", Subject());
        var second = Assert.IsType<SignInOutcome.SignedIn>(
            await Accounts().SignInAsync("github", subject, "Person", Fresh(), default));

        await OperatorAsync("disable", mine.Access.UserId);
        await OperatorAsync("enable", mine.Access.UserId);

        Assert.False(await Sessions.ValidAsync(
            mine.Access.UserId, mine.Access.SessionId, mine.SecurityVersion, default));
        Assert.False(await Sessions.ValidAsync(
            mine.Access.UserId, second.Access.SessionId, second.SecurityVersion, default));
        Assert.True(await Sessions.ValidAsync(
            other.Access.UserId, other.Access.SessionId, other.SecurityVersion, default));
        Assert.True(await CountAsync(
            $"SELECT security_version FROM users WHERE id = '{mine.Access.UserId}'") > mine.SecurityVersion);
    }

    /// <summary>
    /// A command queued for a computer and not yet fetched is withdrawn when its owner is disabled -
    /// not merely hidden by the refusal of the Host's calls. Re-enabled, the computer must not be
    /// handed an instruction the person gave before they were switched off; it is the person's account
    /// that was stopped, and a stopped account's pending work is not deferred, it is cancelled.
    /// </summary>
    [Fact]
    public async Task A_disabled_accounts_queued_command_is_never_delivered()
    {
        var owner = await AdmittedAsync("github", Subject());
        var users = new UserService(Db, Limits.Unlimited, TimeProvider.System);
        var hosts = new HostService(Db);
        var (hostId, _, _) = await users.RegisterHostAsync(owner.Access, "Studio PC", default);
        var host = new HostAccess(hostId, owner.Access.UserId);
        var commandId = await QueueCommandAsync(host);

        var disabled = await OperatorAsync("disable", owner.Access.UserId);

        Assert.Equal(0, disabled.Exit);
        Assert.Equal(FaultCode.AccountDisabled,
            (await Assert.ThrowsAsync<GatewayFault>(() => hosts.SyncAsync(host, []))).Code);
        Assert.Equal("Rejected", Assert.Single(await database.StringsAsync(
            $"SELECT status FROM commands WHERE id = '{commandId}'")));

        await OperatorAsync("enable", owner.Access.UserId);

        Assert.Empty(await hosts.SyncAsync(host, []));
    }

    /// <summary>
    /// Another account's queue is not the disabled one's business, and a command the Host has already
    /// accepted is left alone: it owns it now, as a revoked computer does.
    /// </summary>
    [Fact]
    public async Task Disabling_withdraws_only_undelivered_commands_of_that_account()
    {
        var mine = await AdmittedAsync("github", Subject());
        var theirs = await AdmittedAsync("github", Subject());
        var users = new UserService(Db, Limits.Unlimited, TimeProvider.System);
        var myHost = new HostAccess((await users.RegisterHostAsync(mine.Access, "A", default)).Id, mine.Access.UserId);
        var theirHost = new HostAccess((await users.RegisterHostAsync(theirs.Access, "B", default)).Id, theirs.Access.UserId);
        var undelivered = await QueueCommandAsync(myHost);
        var accepted = await QueueCommandAsync(myHost);
        var others = await QueueCommandAsync(theirHost);
        await database.ExecuteAsync($"UPDATE commands SET status = 'AcceptedByHost' WHERE id = '{accepted}'");

        await OperatorAsync("disable", mine.Access.UserId);

        Assert.Equal("Rejected", await StatusAsync(undelivered));
        Assert.Equal("AcceptedByHost", await StatusAsync(accepted));
        Assert.Equal("PendingDelivery", await StatusAsync(others));
    }

    // ── the operator's command line ─────────────────────────────────────────

    /// <summary>
    /// Every change is written down with who made it. Admissions belong to nobody yet, so they have no
    /// owner; the rest are the account's own history.
    /// </summary>
    [Fact]
    public async Task Every_operator_change_is_audited()
    {
        var subject = Subject();
        var account = await AdmittedAsync("github", subject);
        var id = account.Access.UserId;
        var otherSubject = Subject();

        await OperatorAsync("refuse", $"github:{otherSubject}");
        await OperatorAsync("sessions", "revoke", id);
        await OperatorAsync("disable", id);
        await OperatorAsync("enable", id);

        Assert.Equal(
            ["admission.approved github:" + subject, "sessions.revoked " + id, "account.disabled " + id, "account.enabled " + id],
            await database.StringsAsync(
                $"SELECT CONCAT(action, ' ', target) FROM audit WHERE actor = 'operator' AND (owner_id = '{id}' "
                + $"OR target = 'github:{subject}') ORDER BY id"));
        Assert.Equal("admission.refused", Assert.Single(await database.StringsAsync(
            $"SELECT action FROM audit WHERE target = 'github:{otherSubject}'")));
        Assert.Equal(1, await CountAsync(
            $"SELECT COUNT(*) FROM audit WHERE target = 'github:{otherSubject}' AND owner_id IS NULL"));
    }

    /// <summary>Revoking sessions signs the person out everywhere and leaves them able to sign in.</summary>
    [Fact]
    public async Task Revoking_sessions_signs_out_but_does_not_disable()
    {
        var subject = Subject();
        var account = await AdmittedAsync("github", subject);

        var revoked = await OperatorAsync("sessions", "revoke", account.Access.UserId);

        Assert.Equal(0, revoked.Exit);
        Assert.False(await Sessions.ValidAsync(
            account.Access.UserId, account.Access.SessionId, account.SecurityVersion, default));
        Assert.IsType<SignInOutcome.SignedIn>(await Accounts().SignInAsync(
            "github", subject, "Person", await AfterRevocationAsync(account.Access.UserId), default));
    }

    /// <summary>An id that is not an account is an error to the operator, and writes nothing.</summary>
    [Fact]
    public async Task An_operator_command_on_an_unknown_account_fails()
    {
        var unknown = Ids.New();
        var before = await CountAsync("SELECT COUNT(*) FROM audit");

        foreach (var args in new string[][] { ["disable", unknown], ["enable", unknown], ["sessions", "revoke", unknown] })
        {
            var (exit, output) = await OperatorAsync(args);
            Assert.Equal(1, exit);
            Assert.Contains(unknown, output, StringComparison.Ordinal);
        }

        Assert.Equal(before, await CountAsync("SELECT COUNT(*) FROM audit"));
    }

    /// <summary>Anything it does not understand prints how to use it and fails, and changes nothing.</summary>
    [Theory]
    [InlineData]
    [InlineData("bogus")]
    [InlineData("approve")]
    [InlineData("approve", "github")]
    [InlineData("approve", ":123")]
    [InlineData("approve", "github:")]
    [InlineData("approve", "GitHub:123")]
    [InlineData("approve", "github:1 2")]
    [InlineData("approve", "github:123", "extra")]
    [InlineData("disable")]
    [InlineData("sessions")]
    [InlineData("sessions", "list", "x")]
    [InlineData("admissions", "extra")]
    public async Task Bad_operator_commands_print_usage_and_fail(params string[] args)
    {
        var before = await CountAsync("SELECT COUNT(*) FROM admissions");

        var (exit, output) = await OperatorAsync(args);

        Assert.NotEqual(0, exit);
        Assert.Contains("Usage", output, StringComparison.Ordinal);
        Assert.Equal(before, await CountAsync("SELECT COUNT(*) FROM admissions"));
    }

    /// <summary>
    /// What a stranger calls themselves is printed on the operator's terminal. A display name carrying
    /// escape sequences must not be able to rewrite that screen.
    /// </summary>
    [Fact]
    public async Task The_waiting_list_does_not_pass_control_characters_to_the_terminal()
    {
        var subject = Subject();
        await Accounts().SignInAsync("github", subject, "Eve\u001b[2J\u0007\nFAKE", Fresh(), default);

        var (exit, output) = await OperatorAsync("admissions");

        Assert.Equal(0, exit);
        var line = Assert.Single(output.Split('\n'), l => l.Contains(subject, StringComparison.Ordinal));
        Assert.Contains("Eve", line, StringComparison.Ordinal);
        Assert.DoesNotContain(output, c => c is '\u001b' or '\u0007');
        Assert.DoesNotContain("FAKE\n", output + "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// The binary's own entry point: <c>admin</c> as the first argument runs the command and exits,
    /// instead of starting a web server that would then be waited on for ever. Needs only the database.
    /// </summary>
    [Fact]
    public async Task The_admin_argument_runs_the_command_line_and_exits()
    {
        var subject = Subject();
        await Accounts().SignInAsync("github", subject, "Waiting Person", Fresh(), default);

        var (exit, output) = await RunBinaryAsync(["admin", "admissions"], database.ConnectionString);
        Assert.Equal(0, exit);
        Assert.Contains($"github:{subject}", output, StringComparison.Ordinal);

        var (missing, message) = await RunBinaryAsync(["admin", "admissions"], connection: null);
        Assert.NotEqual(0, missing);
        Assert.Contains("ENACTIVE_REMOTE_DB", message, StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<string> StatusAsync(string commandId)
        => Assert.Single(await database.StringsAsync($"SELECT status FROM commands WHERE id = '{commandId}'"));

    private async Task<string> QueueCommandAsync(HostAccess host)
    {
        var commandId = Guid.NewGuid().ToString();
        var sealedCancel = Envelope.Seal(RandomNumberGenerator.GetBytes(32), 1, Encoding.UTF8.GetBytes("cancel"), []);

        await database.ExecuteAsync(
            """
            INSERT INTO commands (owner_id, id, host_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES (@owner, @id, @host, 'CancelRun', @payload, SHA2(@id, 256), 'PendingDelivery',
                    UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL 1 DAY)
            """,
            ("@owner", host.OwnerId), ("@id", commandId), ("@host", host.HostId),
            ("@payload", RemoteJson.Serialize(new CancelRunPayload(Ids.New(), sealedCancel))));

        return commandId;
    }

    /// <summary>Runs the gateway binary the way an operator does, with a deadline: a hang is a failure.</summary>
    private static async Task<(int Exit, string Output)> RunBinaryAsync(string[] args, string? connection)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);

        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove("ENACTIVE_REMOTE_DB");

        if (connection is not null)
        {
            start.Environment["ENACTIVE_REMOTE_DB"] = connection;
        }

        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The admin command did not exit: it started the web application.");
        }

        return (process.ExitCode, await output + await error);
    }
}
