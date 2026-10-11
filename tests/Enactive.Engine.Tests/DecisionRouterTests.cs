namespace Enactive.Engine.Tests;

using Enactive.Application;
using Enactive.Core.Permissions;
using Xunit;

/// <summary>
/// Asking a person what a run needs answered (DecisionRouter): which answers are offered, what an answer remembers and
/// for whom, one question at a time, and a withdrawn question never answered. These lived in the window's card code,
/// where no test reached them, and the service's browser needs exactly the same.
/// </summary>
public sealed class DecisionRouterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-router", Guid.NewGuid().ToString("N"));
    private readonly ApprovalStore _approvals;
    private readonly SessionApprovals _session = new();

    public DecisionRouterTests()
    {
        Directory.CreateDirectory(_root);
        _approvals = new ApprovalStore(Path.Combine(_root, "permissions.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private DecisionRequest Ask(string tool = "write_file", bool sessionOnly = false, bool explicitAnswer = false)
        => new(Guid.NewGuid(), "Approve", "detail", [new("allow", "Allow"), new("deny", "Deny")], "allow",
            Subject: tool, SessionOnly: sessionOnly,
            Action: new(Guid.NewGuid(), Guid.NewGuid().ToString(), tool, "{}", _root))
        { RequiresExplicitAnswer = explicitAnswer };

    /// <summary>A presenter that records what it was shown and lets the test click.</summary>
    private sealed class Card : IDecisionPresenter
    {
        public readonly List<(DecisionRequest Request, IReadOnlyList<DecisionChoice> Choices, Func<DecisionChoice, bool> Answer)> Shown = new();
        public readonly List<Guid> Cleared = new();
        public readonly SemaphoreSlim Up = new(0);

        public Task ShowAsync(DecisionRequest request, IReadOnlyList<DecisionChoice> choices, Func<DecisionChoice, bool> answer)
        {
            lock (Shown) Shown.Add((request, choices, answer));
            Up.Release();
            return Task.CompletedTask;
        }

        public Task ClearAsync(DecisionRequest request)
        {
            lock (Cleared) Cleared.Add(request.Id);
            return Task.CompletedTask;
        }

        public async Task<bool> ClickAsync(string label)
        {
            await Up.WaitAsync(TimeSpan.FromSeconds(10));
            var (_, choices, answer) = Shown[^1];
            return answer(choices.Single(c => c.Label == label));
        }
    }

    // ── what is offered ─────────────────────────────────────────────────────

    [Fact]
    public void A_tool_call_offers_its_options_and_allow_for_the_session_and_the_workspace()
        => Assert.Equal(
            ["Allow", "Deny", "Allow (session)", "Allow (workspace)", "Allow (workspace, unwatched runs too)"],
            DecisionRouter.ChoicesFor(Ask()).Select(c => c.Label));

    /// <summary>A shell remembered for good is unlimited command execution from one click: only the session is offered.</summary>
    [Fact]
    public void A_shell_is_offered_for_the_session_only()
        => Assert.Equal(["Allow", "Deny", "Allow (session)"], DecisionRouter.ChoicesFor(Ask("run_command", sessionOnly: true)).Select(c => c.Label));

    /// <summary>A phone's question needs a fresh answer every time: nothing it is offered may be remembered.</summary>
    [Fact]
    public void A_question_that_needs_a_fresh_answer_offers_nothing_to_remember()
        => Assert.All(DecisionRouter.ChoicesFor(Ask(explicitAnswer: true)), c => Assert.Equal(RememberAs.Nothing, c.Remember));

    [Fact]
    public void A_question_with_no_allow_offers_nothing_more_than_its_options()
    {
        var question = Ask() with { Options = [new("retry", "Retry"), new("stop", "Stop")] };

        Assert.Equal(["Retry", "Stop"], DecisionRouter.ChoicesFor(question).Select(c => c.Label));
    }

    // ── what an answer does ─────────────────────────────────────────────────

    [Fact]
    public async Task An_answer_is_the_run_s_decision_and_the_card_comes_down()
    {
        var card = new Card();
        var router = new DecisionRouter(card, _session, _approvals);
        var question = Ask();

        var asked = router.RequestAsync(question, CancellationToken.None);
        Assert.True(await card.ClickAsync("Deny"));

        Assert.Equal("deny", (await asked).OptionId);
        Assert.Contains(question.Id, card.Cleared);
        Assert.False(router.IsPending);
        Assert.False(_approvals.Approves(_root, "write_file"));
    }

    /// <summary>
    /// On the service several people answer through one router each; what one of them remembers is theirs
    /// (ApprovalStore, rule 5) - not the account's, nor anyone else's.
    /// </summary>
    [Theory]
    [InlineData("Allow (workspace)", false)]
    [InlineData("Allow (workspace, unwatched runs too)", true)]
    public async Task Allow_for_the_workspace_is_remembered_as_the_person_who_answered(string label, bool unwatched)
    {
        var card = new Card();
        var router = new DecisionRouter(card, _session, _approvals, principal: "alice");

        var asked = router.RequestAsync(Ask(), CancellationToken.None);
        await card.ClickAsync(label);

        Assert.Equal("allow", (await asked).OptionId);
        Assert.True(_approvals.Approves(_root, "write_file", principal: "alice"));
        Assert.Equal(unwatched, _approvals.Approves(_root, "write_file", unwatched: true, principal: "alice"));
        Assert.False(_approvals.Approves(_root, "write_file"), "A person's answer was remembered as the account's.");
    }

    [Fact]
    public async Task Allow_for_the_session_is_remembered_for_the_session_only()
    {
        var card = new Card();
        var router = new DecisionRouter(card, _session, _approvals);
        var question = Ask();

        var asked = router.RequestAsync(question, CancellationToken.None);
        await card.ClickAsync("Allow (session)");
        await asked;

        Assert.True(_session.Approves(question));
        Assert.False(_approvals.Approves(_root, "write_file"));
    }

    /// <summary>
    /// A presenter that offers "for the workspace" on a shell - a browser page put together by hand - still does not
    /// get a shell remembered for good.
    /// </summary>
    [Fact]
    public async Task A_shell_is_not_remembered_for_good_whatever_the_presenter_offered()
    {
        var question = Ask("run_command", sessionOnly: true);
        var forged = new ForgedCard(new DecisionChoice("Allow (workspace)", "allow", RememberAs.Workspace));
        var router = new DecisionRouter(forged, _session, _approvals);

        Assert.Equal("allow", (await router.RequestAsync(question, CancellationToken.None)).OptionId);

        Assert.False(_approvals.Approves(_root, "run_command"));
    }

    private sealed class ForgedCard(DecisionChoice choice) : IDecisionPresenter
    {
        public Task ShowAsync(DecisionRequest request, IReadOnlyList<DecisionChoice> choices, Func<DecisionChoice, bool> answer)
        {
            answer(choice);
            return Task.CompletedTask;
        }

        public Task ClearAsync(DecisionRequest request) => Task.CompletedTask;
    }

    // ── a withdrawn question ────────────────────────────────────────────────

    /// <summary>
    /// A run cancelled while its question was up: the card comes down, nothing is pending - "a decision is pending"
    /// once held the next run back for a task already stopped - and a late click answers nothing.
    /// </summary>
    [Fact]
    public async Task A_question_withdrawn_comes_down_and_a_late_answer_does_nothing()
    {
        var card = new Card();
        var router = new DecisionRouter(card, _session, _approvals);
        var question = Ask();
        using var cancel = new CancellationTokenSource();

        var asked = router.RequestAsync(question, cancel.Token);
        await card.Up.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(router.IsPending);
        card.Up.Release();

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked);

        Assert.False(router.IsPending);
        Assert.Contains(question.Id, card.Cleared);
        Assert.False(await card.ClickAsync("Allow (workspace)"));
        Assert.False(_approvals.Approves(_root, "write_file"), "A click on a withdrawn question remembered an approval.");
    }

    /// <summary>One question at a time: the second run's question goes up when the first is answered, not over it.</summary>
    [Fact]
    public async Task A_second_question_waits_for_the_first()
    {
        var card = new Card();
        var router = new DecisionRouter(card, _session, _approvals);

        var first = router.RequestAsync(Ask(), CancellationToken.None);
        var second = router.RequestAsync(Ask("read_file"), CancellationToken.None);
        await card.Up.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(card.Shown);
        card.Up.Release();

        await card.ClickAsync("Allow");
        await first;
        await card.ClickAsync("Deny");

        Assert.Equal("deny", (await second).OptionId);
        Assert.Equal(2, card.Shown.Count);
    }
}
