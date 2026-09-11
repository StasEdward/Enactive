namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// FIX_PLAN §9an. Found in a real scheduled run on 2026-09-11: schedule <c>test1</c> fired at 00:14
/// on the Execute tier, whose AskBefore list is run_command, run_powershell, git, docker. Nobody is
/// watching a scheduled run, so the handler refused everything it was asked - correctly, and as the
/// schedules window states in words before the schedule is saved.
///
/// <para>The model was handed all thirteen registered tools anyway. Four of them could not succeed
/// once, and nothing said so, so it found out the only way it could: git diff, git diff, git diff,
/// run_command "git diff", git diff, git status. Seven calls to the worker model and 28 167 prompt
/// tokens to discover a decision taken before the first of them.</para>
///
/// <para>The rule these tests hold in place: <b>a tool whose gate can only end in refusal is not
/// advertised</b> - and, exactly as importantly, a tool somebody can still answer for IS.</para>
/// </summary>
public sealed class WithheldToolTests
{
    private static string[] ToolsShownIn(FakeChatProvider provider)
        => provider.Requests.Last().Tools?.Select(t => t.Name).ToArray() ?? Array.Empty<string>();

    /// <summary>
    /// A worker that carries a shell, a policy that asks before using it, and nobody to ask: the
    /// shell is not on the list the model is given.
    /// </summary>
    [Fact]
    public async Task An_unattended_run_is_not_offered_a_tool_nobody_can_approve()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"Look around","steps":[]}"""),
            Turn.Says("Nothing I can do without a shell."));

        await fx.RunAsync(
            fx.Build(provider,
                EngineFixture.WorkerWith("read_file", "list_dir", "run_command"),
                policy: new PermissionPolicy(
                    PermissionLevel.Execute, new[] { "*" }, new[] { "run_command" }),
                decisions: new UnattendedDecisionHandler()),
            "see what is here");

        var shown = ToolsShownIn(provider);

        Assert.DoesNotContain("run_command", shown);

        // And the rest of the role survives: this withholds what cannot work, not everything.
        Assert.Contains("read_file", shown);
        Assert.Contains("list_dir", shown);
    }

    /// <summary>
    /// The line the whole change balances on. The SAME policy and the SAME role, with somebody there
    /// to answer: the tool is offered, because the question is real and a person who refuses once
    /// may allow the next time. Withholding here would take away the approval prompt that IS the
    /// feature.
    /// </summary>
    [Fact]
    public async Task A_watched_run_is_still_offered_everything_it_may_be_asked_about()
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "deny";  // even a handler that says no, because it MIGHT say yes

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"Look around","steps":[]}"""),
            Turn.Says("done"));

        await fx.RunAsync(
            fx.Build(provider,
                EngineFixture.WorkerWith("read_file", "list_dir", "run_command"),
                policy: new PermissionPolicy(
                    PermissionLevel.Execute, new[] { "*" }, new[] { "run_command" })),
            "see what is here");

        Assert.Contains("run_command", ToolsShownIn(provider));
    }

    /// <summary>
    /// A tool the policy denies outright is withheld from every run, watched or not. Nothing can
    /// turn a Deny into an approval, so advertising one is the same waste with no question behind
    /// it at all.
    /// </summary>
    [Fact]
    public async Task A_denied_tool_is_withheld_even_when_somebody_is_watching()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"Look around","steps":[]}"""),
            Turn.Says("done"));

        await fx.RunAsync(
            fx.Build(provider,
                EngineFixture.WorkerWith("read_file", "list_dir", "git"),
                policy: new PermissionPolicy(
                    PermissionLevel.Execute, new[] { "*" }, Array.Empty<string>())
                { Deny = new[] { "git" } }),
            "see what is here");

        Assert.DoesNotContain("git", ToolsShownIn(provider));
    }

    /// <summary>
    /// Withheld VISIBLY. A run that quietly cannot use a shell and does not say so is a worse
    /// failure than the one being fixed: the report would name a plan that could never have worked,
    /// with no reason in it anywhere.
    /// </summary>
    [Fact]
    public async Task The_run_record_says_what_was_withheld_and_why()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"Look around","steps":[]}"""),
                    Turn.Says("done")),
                EngineFixture.WorkerWith("read_file", "run_command"),
                policy: new PermissionPolicy(
                    PermissionLevel.Execute, new[] { "*" }, new[] { "run_command" }),
                decisions: new UnattendedDecisionHandler()),
            "see what is here");

        var text = events.Text();

        Assert.Contains("run_command", text, StringComparison.Ordinal);
        Assert.Contains(ToolOffers.Unanswerable, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second half of the rule. A withheld tool can still be CALLED - a name remembered from
    /// earlier in the transcript, or invented - and when it is, the answer is the reason it was
    /// withheld. No decision request is raised, because asking a handler that cannot say yes costs
    /// a round trip to reach the same refusal.
    /// </summary>
    [Fact]
    public async Task Calling_a_withheld_tool_anyway_is_refused_without_asking_anybody()
    {
        using var fx = new EngineFixture();
        var unattended = new UnattendedDecisionHandler();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"Run it","steps":[]}"""),
            Turn.Calls1("run_command", """{"command":"git diff"}"""),
            Turn.Says("I could not run it."));

        var events = await fx.RunAsync(
            fx.Build(provider,
                EngineFixture.WorkerWith("read_file", "run_command"),
                policy: new PermissionPolicy(
                    PermissionLevel.Execute, new[] { "*" }, new[] { "run_command" }),
                decisions: unattended),
            "run the diff");

        // Nobody was asked. This is the token saving, and it is the whole point: the handler is the
        // thing a refusal used to have to travel to.
        Assert.Empty(unattended.Refusals);
        Assert.False(events.Has(EventKind.DecisionRequested), events.Text());

        // And the model is told it will not become permitted, so it stops trying to work around it.
        var toldTheModel = provider.Requests.Last().Messages
            .Where(m => m.Role == Core.Chat.ChatRole.Tool)
            .Select(m => m.Content ?? "")
            .ToArray();

        Assert.Contains(toldTheModel, m => m.Contains("not permitted for this run", StringComparison.Ordinal));
    }

    /// <summary>
    /// Refused is still failed. A withheld tool must not turn into a silence that lets the step
    /// finish green over an action that never happened - the property DeniedToolTests established
    /// and this change must not quietly undo.
    /// </summary>
    [Fact]
    public async Task A_withheld_tool_called_anyway_still_fails_the_run()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"Run it","steps":[]}"""),
                    Turn.Calls1("run_command", """{"command":"git diff"}"""),
                    Turn.Says("All done!")),
                EngineFixture.WorkerWith("read_file", "run_command"),
                policy: new PermissionPolicy(
                    PermissionLevel.Execute, new[] { "*" }, new[] { "run_command" }),
                decisions: new UnattendedDecisionHandler()),
            "run the diff");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// The case the tool offer does NOT cover, and the one reachable in the configuration §9an
    /// recommends for schedules. At Autonomous the shell is allowed outright and rightly offered -
    /// and then every write it aims outside the workspace is a question nobody is awake to answer.
    ///
    /// <para>Both halves are asserted, because either alone would pass over a broken gate: nobody
    /// was asked, AND the file was not written. A short-circuit that skipped the question and let
    /// the command run would be very much worse than the round trip it saves.</para>
    /// </summary>
    [Fact]
    public async Task An_unattended_run_does_not_ask_whether_it_may_write_outside_the_workspace()
    {
        using var fx = new EngineFixture();
        var unattended = new UnattendedDecisionHandler();

        var elsewhere = Path.Combine(
            Path.GetTempPath(), "enactive-withheld-geo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        var target = Path.Combine(elsewhere, "out.txt");

        try
        {
            var provider = new FakeChatProvider(
                Turn.Says("""{"disposition":"quick_action","title":"Write it","steps":[]}"""),
                Turn.Calls1("run_command",
                    System.Text.Json.JsonSerializer.Serialize(
                        new { command = $"echo hello> \"{target}\"" })),
                Turn.Says("Not written."));

            await fx.RunAsync(
                fx.Build(provider, EngineFixture.Role("developer"),
                    // Autonomous: the shell is allowed outright, so it IS offered and the call gets
                    // as far as the geography gate. Nothing is withheld in this run.
                    policy: new PermissionPolicy(
                        PermissionLevel.Autonomous, new[] { "*" }, Array.Empty<string>()),
                    decisions: unattended),
                "write the file");

            Assert.Empty(unattended.Refusals);
            Assert.False(File.Exists(target),
                "The question was skipped and the command wrote outside the workspace anyway.");
        }
        finally
        {
            try { Directory.Delete(elsewhere, recursive: true); } catch { /* a temp folder */ }
        }
    }

    // ── the rule itself, away from an engine ────────────────────────────────

    /// <summary>
    /// The two reasons are different facts and must read as different facts. A person looking at a
    /// report needs to know whether the tool is forbidden or merely unanswerable at this hour -
    /// the first is a policy to change, the second is a schedule to run differently.
    /// </summary>
    [Fact]
    public void A_blocked_tool_and_an_unanswerable_one_are_not_the_same_reason()
    {
        var offer = ToolOffers.For(
            new[] { "read_file", "git", "run_command" },
            tool => tool switch
            {
                "git" => PermissionDecision.Deny,
                "run_command" => PermissionDecision.Ask,
                _ => PermissionDecision.Allow
            },
            approvalIsPossible: false);

        Assert.Equal(new[] { "read_file" }, offer.Offered);
        Assert.Equal(ToolOffers.Blocked, offer.Reason("git"));
        Assert.Equal(ToolOffers.Unanswerable, offer.Reason("run_command"));
        Assert.NotEqual(ToolOffers.Blocked, ToolOffers.Unanswerable);
    }

    /// <summary>
    /// Four tools withheld for one reason are one fact. Repeating it four times invites the reader
    /// to go looking for four causes.
    /// </summary>
    [Fact]
    public void The_sentence_groups_tools_by_the_reason_they_were_withheld()
    {
        var offer = ToolOffers.For(
            new[] { "git", "docker", "read_file" },
            tool => tool == "read_file" ? PermissionDecision.Allow : PermissionDecision.Ask,
            approvalIsPossible: false);

        var sentence = offer.Sentence;

        Assert.NotNull(sentence);
        Assert.Contains("git, docker", sentence, StringComparison.Ordinal);

        // Once, not once per tool.
        Assert.Equal(
            1,
            sentence!.Split(ToolOffers.Unanswerable).Length - 1);
    }

    /// <summary>
    /// Nothing withheld says nothing. A run that kept nothing back must not put a line in its
    /// record claiming it did - the record is read by somebody deciding whether a run was honest.
    /// </summary>
    [Fact]
    public void A_run_that_withheld_nothing_says_nothing()
    {
        var offer = ToolOffers.For(
            new[] { "read_file", "list_dir" },
            _ => PermissionDecision.Allow,
            approvalIsPossible: false);

        Assert.Null(offer.Sentence);
        Assert.Empty(offer.Withheld);
    }

    /// <summary>
    /// A handler has to say NO on purpose. The default is "a person might answer", because getting
    /// this wrong permissively costs tokens and getting it wrong the other way takes away a question
    /// somebody was going to answer.
    /// </summary>
    [Fact]
    public void A_handler_that_says_nothing_is_assumed_to_be_answerable()
    {
        // Through the INTERFACE, which is how the engine reads it and the only place a default
        // implementation exists at all. A handler that wants the other answer declares it, as
        // UnattendedDecisionHandler does.
        Assert.True(((IDecisionHandler)new ScriptedDecisionHandler()).CanApprove);
        Assert.False(((IDecisionHandler)new UnattendedDecisionHandler()).CanApprove);
    }
}
