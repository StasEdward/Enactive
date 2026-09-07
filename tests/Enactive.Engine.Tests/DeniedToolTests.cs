namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Reported by Stas, 2026-09-07: the Code Review template failed because its call to git was
/// refused.
///
/// <para>Two defects in one run, and the second is the general one.</para>
///
/// <para><b>The template forbade what its own goal required.</b> Code Review's default scope is
/// "everything that has changed since the last commit" — it asks the model to review a diff — and
/// its ceiling denied <c>git</c>. That is not caution; it is a template that cannot do its job. It
/// never prevented anything either, since <c>run_command</c> is allowed in the same ceiling and
/// <c>git diff</c> runs perfectly well through it. The note in <c>BuiltinTemplates</c> saying a deny
/// list is not a sandbox was already there, and the template contradicted it.</para>
///
/// <para><b>A blocked tool was reported to the model as a person's refusal.</b> The two cases were
/// distinguished, recorded in the journal — and then collapsed into one sentence on the single path
/// where the difference changes what the model does next. Told "the user did not permit this", a
/// model stops and apologises, which is the correct response to a person saying no. Told a tool is
/// off for this run, it should do the job another way. It could not tell which had happened.</para>
/// </summary>
public sealed class DeniedToolTests
{
    private static PermissionPolicy Denying(params string[] tools)
        => new(PermissionLevel.Execute, new[] { "*" }, Array.Empty<string>()) { Deny = tools };

    // ── what the model is told ──────────────────────────────────────────────

    /// <summary>
    /// A tool blocked by policy says so, says it will stay blocked, and says what to do instead.
    /// Anything less and the model spends its remaining steps asking for the same tool.
    /// </summary>
    [Fact]
    public async Task A_tool_blocked_by_policy_says_it_will_stay_blocked()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"Look at the diff","steps":[]}"""),
            Turn.Calls1("git", """{"args":["diff"]}"""),
            Turn.Says("I could not read the diff."));

        await fx.RunAsync(
            fx.Build(provider, EngineFixture.Role("developer"), policy: Denying("git")),
            "review what changed");

        var toldTheModel = provider.Requests.Last().Messages
            .Where(m => m.Role == Core.Chat.ChatRole.Tool)
            .Select(m => m.Content ?? "")
            .ToArray();

        Assert.Contains(toldTheModel, m => m.Contains("not permitted for this run", StringComparison.Ordinal));
        Assert.Contains(toldTheModel, m => m.Contains("Do not call it again", StringComparison.Ordinal));

        // And specifically NOT the sentence that means a person refused.
        Assert.DoesNotContain(toldTheModel, m => m.Contains("the user did not permit", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half: a person who declines an approval is still reported as a person. Collapsing
    /// this one into "policy" would be the same error in the other direction — a model told the
    /// policy forbids something stops offering it, when the answer was "not this time".
    /// </summary>
    [Fact]
    public async Task A_tool_the_person_declined_still_reads_as_a_persons_answer()
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "deny";

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"Run it","steps":[]}"""),
            Turn.Calls1("run_command", """{"command":"dotnet build"}"""),
            Turn.Says("Not run."));

        // Ask-before puts the call in front of the person, who says no.
        var policy = new PermissionPolicy(
            PermissionLevel.Execute, new[] { "*" }, new[] { "run_command" });

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), policy: policy), "build it");

        var toldTheModel = provider.Requests.Last().Messages
            .Where(m => m.Role == Core.Chat.ChatRole.Tool)
            .Select(m => m.Content ?? "")
            .ToArray();

        Assert.Contains(toldTheModel, m => m.Contains("the user did not permit", StringComparison.Ordinal));
        Assert.DoesNotContain(toldTheModel, m => m.Contains("not permitted for this run", StringComparison.Ordinal));
    }

    /// <summary>
    /// Either way the step does not finish green over an action that never happened. That property
    /// predates this change and must survive it.
    /// </summary>
    [Fact]
    public async Task A_refused_call_is_still_a_failure_not_a_silence()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"Commit it","steps":[]}"""),
                    Turn.Calls1("git", """{"args":["commit","-m","done"]}"""),
                    Turn.Says("All done!")),
                EngineFixture.Role("developer"), policy: Denying("git")),
            "commit the change");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }

    // ── the template that forbade its own job ───────────────────────────────

    /// <summary>
    /// Code Review reviews what has changed. Denying git meant the template could not see it.
    /// </summary>
    [Fact]
    public void A_review_of_what_changed_may_look_at_what_changed()
    {
        var review = BuiltinTemplates.All.Single(t => t.Id == "code-review");

        Assert.DoesNotContain("git", review.Ceiling.DenyList);

        // It is still a review: it may write its report and nothing else.
        Assert.Contains("edit_file", review.Ceiling.DenyList);
        Assert.Contains("move_file", review.Ceiling.DenyList);
    }

    /// <summary>
    /// The general rule, as far as a test can state it: a template must not deny a tool whose name
    /// its own goal or parameter defaults tell the model to use. That is a contradiction the person
    /// running it meets as a hard failure, with nothing saying the template was written that way.
    ///
    /// <para><b>It would not have caught the one that happened</b>, and that is worth writing down
    /// rather than leaving for somebody to assume. Code Review's goal never says "git" — it says
    /// "everything that has changed since the last commit", which requires git without naming it.
    /// A test can check the words; only a person can check the meaning. This catches the literal
    /// case, which is the common one, and the honest scope of it is exactly that.</para>
    /// </summary>
    [Fact]
    public void No_built_in_denies_a_tool_its_own_words_tell_the_model_to_use()
    {
        foreach (var template in BuiltinTemplates.All)
        {
            var words = template.Goal + " " + string.Join(
                " ", template.ParameterList.Select(p => $"{p.Description} {p.Default}"));

            foreach (var denied in template.Ceiling.DenyList)
                Assert.False(
                    words.Contains(denied, StringComparison.OrdinalIgnoreCase),
                    $"'{template.Id}' tells the model to use '{denied}' and then denies it. "
                    + "A template that forbids what it asks for cannot do its job, and the person "
                    + "running it finds out as a failed step.");
        }
    }

    /// <summary>
    /// Denying one shell and not the other confines nothing at all.
    ///
    /// <para>The two shell tools do the same job — <c>run_powershell</c> exists so a script does not
    /// have to survive cmd quoting, not to be a weaker <c>run_command</c>. A template that denies one
    /// and leaves the other reads, in a settings window, exactly like a template that cannot run
    /// commands, and is not one. Documentation Sync denies both, which is what makes its restriction
    /// mean something; this keeps the next one honest.</para>
    ///
    /// <para>The wider truth this does NOT fix, and which the deny lists must not be read as
    /// solving: while either shell is allowed, denying <c>git</c> or <c>docker</c> steers the model
    /// away from a tool, it does not confine it — <c>git diff</c> runs through a shell perfectly
    /// well. That is worth keeping where it states an intention (a bug fix should not commit itself)
    /// and worth nothing as a guarantee.</para>
    /// </summary>
    [Fact]
    public void A_template_that_denies_one_shell_denies_the_other()
    {
        foreach (var template in BuiltinTemplates.All)
        {
            var deny = template.Ceiling.DenyList;
            var cmd = deny.Contains("run_command", StringComparer.OrdinalIgnoreCase);
            var pwsh = deny.Contains("run_powershell", StringComparer.OrdinalIgnoreCase);

            Assert.True(cmd == pwsh,
                $"'{template.Id}' denies {(cmd ? "run_command" : "run_powershell")} and allows "
                + $"{(cmd ? "run_powershell" : "run_command")}. They do the same job, so this "
                + "restricts nothing while looking like it restricts running commands.");
        }
    }
}
