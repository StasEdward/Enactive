namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Application;
using Enactive.Core.Rules;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Plan Phase 5.2: before a person keeps a rule for one workspace, it is checked against the rules every workspace is given
/// - both would otherwise reach every run here, the worker told two things it cannot both do. One question to a model, only
/// when there are Global rules; a contradiction holds the keep until the person keeps it anyway or changes it. An aid, not
/// a bar: an answer that fails, or none, keeps the rule as before.
/// </summary>
public sealed class ARuleAgainstAGlobalOneIsHeldTests
{
    private static RuleEntry Confirmed(string text, RuleScope scope)
        => new(Guid.NewGuid(), scope, RuleHeading.Interpretations, text, null, DateTimeOffset.UnixEpoch, RuleStatus.Confirmed);

    private const string Global = "Always run the tests after a change.";
    private const string Against = "Never run the tests: they take an hour.";

    [Fact]
    public async Task The_model_is_asked_only_with_global_rules_and_its_answer_is_read_field_by_field()
    {
        var silent = new FakeChatProvider();
        Assert.Null(await RuleConsistency.CheckAsync(Against, [], silent, "m", CancellationToken.None));
        Assert.Empty(silent.Requests);

        var says = new FakeChatProvider(Turn.Says("""{"contradicts": 1, "why": "one says run the tests, the other never"}"""));
        var conflict = await RuleConsistency.CheckAsync(Against, [Confirmed(Global, RuleScope.Global)], says, "m", CancellationToken.None);
        Assert.Equal((Global, "one says run the tests, the other never"), (conflict!.GlobalRule, conflict.Why));
        var asked = string.Join("\n", Assert.Single(says.Requests).Messages.Select(m => m.Content));
        Assert.Contains("1. " + Global, asked, StringComparison.Ordinal);
        Assert.Contains(Against, asked, StringComparison.Ordinal);

        foreach (var answer in new[] { """{"contradicts": 0, "why": ""}""", """{"contradicts": 7}""", "no idea", "" })
            Assert.Null(await RuleConsistency.CheckAsync(Against, [Confirmed(Global, RuleScope.Global)],
                new FakeChatProvider(Turn.Says(answer)), "m", CancellationToken.None));
    }

    [Fact]
    public async Task A_contradicted_keep_waits_for_the_person_and_a_global_keep_or_a_failed_question_does_not()
    {
        using var fx = new EngineFixture();
        var workspace = new JsonRuleStore(fx.PathOf(Path.Combine(".enactive", "rules.json")));
        var global = JsonRuleStore.Global(fx.PathOf("machine"));
        await global.SaveAsync(Confirmed(Global, RuleScope.Global), CancellationToken.None);
        var candidate = new RuleEntry(Guid.NewGuid(), RuleScope.Workspace, RuleHeading.Deviations, "the tests took an hour", Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, RuleStatus.Candidate) { Source = "review:x" };
        await workspace.SaveAsync(candidate, CancellationToken.None);

        var asked = 0;
        var panel = new LessonsViewModel(new BookLessons(new RuleBook(workspace, global)), "Lessons", (text, rules, ct) =>
        {
            asked++;
            Assert.Equal(Global, Assert.Single(rules).Text);
            return Task.FromResult<RuleConflict?>(new RuleConflict(Global, "one says run them, the other never"));
        });
        await panel.RefreshAsync();

        panel.Candidates[0].Text = Against;
        await panel.Candidates[0].KeepCommand.ExecuteAsync();
        Assert.True(panel.HasConflict);
        Assert.Contains(Global, panel.Conflict, StringComparison.Ordinal);
        Assert.Equal(RuleStatus.Candidate, Assert.Single(await workspace.ListAsync(CancellationToken.None)).Status);

        panel.ChangeItCommand.Execute(null);
        Assert.False(panel.HasConflict);
        await panel.Candidates[0].KeepCommand.ExecuteAsync();
        await panel.KeepAnywayCommand.ExecuteAsync();
        Assert.False(panel.HasConflict);
        Assert.Equal((RuleStatus.Confirmed, Against), Assert.Single(await workspace.ListAsync(CancellationToken.None)) is var kept ? (kept.Status, kept.Text) : default);
        Assert.Equal(2, asked);

        // For every workspace: not asked - the check is of a workspace rule against the Global ones.
        panel.NewText = "Say which model wrote the report.";
        panel.NewGlobal = true;
        await panel.AddCommand.ExecuteAsync();
        Assert.Equal(2, asked);
        Assert.Equal(2, (await global.ListAsync(CancellationToken.None)).Count);

        // A question that fails keeps the rule, as if there were none to ask.
        var failing = new LessonsViewModel(new BookLessons(new RuleBook(workspace, global)), "Lessons",
            (_, _, _) => throw new HttpRequestException("model down"));
        await failing.RefreshAsync();
        failing.NewText = "Write the report in English.";
        await failing.AddCommand.ExecuteAsync();
        Assert.False(failing.HasConflict);
        Assert.Contains(await workspace.ListAsync(CancellationToken.None), r => r.Text == "Write the report in English.");
    }

    [Fact]
    public async Task An_answer_cut_off_at_its_limit_is_not_read_and_the_rule_is_kept_saying_so()
    {
        var cut = new FakeChatProvider(new Turn("""{"contradicts": 0, "why": "none of th""", FinishReason: "length"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RuleConsistency.CheckAsync(Against, [Confirmed(Global, RuleScope.Global)], cut, "m", CancellationToken.None));
        Assert.Equal(RuleConsistency.AnswerTokens, Assert.Single(cut.Requests).OutputTokenLimit);

        using var fx = new EngineFixture();
        var workspace = new JsonRuleStore(fx.PathOf(Path.Combine(".enactive", "rules.json")));
        var global = JsonRuleStore.Global(fx.PathOf("machine"));
        await global.SaveAsync(Confirmed(Global, RuleScope.Global), CancellationToken.None);
        var panel = new LessonsViewModel(new BookLessons(new RuleBook(workspace, global)), "Lessons", (text, rules, ct) =>
            RuleConsistency.CheckAsync(text, rules, new FakeChatProvider(new Turn("{\"contra", FinishReason: "length")), "m", ct));
        await panel.RefreshAsync();
        panel.NewText = Against;
        await panel.AddCommand.ExecuteAsync();
        Assert.False(panel.HasConflict);
        Assert.Contains("cut off at its", panel.Said, StringComparison.Ordinal);
        Assert.Single(await workspace.ListAsync(CancellationToken.None));
    }
}
