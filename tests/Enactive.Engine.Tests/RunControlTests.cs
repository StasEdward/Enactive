namespace Enactive.Engine.Tests;

using Enactive.Application;
using Enactive.Application.Projections;
using Enactive.Core.Events;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// The parts of running a task that every host has around a run (server plan, step 0.5): what is checked before it
/// starts, which runs are in flight and where, what is happening in one now, and what stopping would cut short. They
/// lived in the window's code-behind, where only the window could use them and no test could reach them.
/// </summary>
public sealed class RunControlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-runcontrol", Guid.NewGuid().ToString("N"));

    public RunControlTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private static readonly Guid Run = Guid.NewGuid();

    private static WorkEvent Ev(EventKind kind, string summary, string? payload = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), Run, DateTimeOffset.UtcNow, kind, summary, payload);

    // ── before a run starts ─────────────────────────────────────────────────

    /// <summary>A typo in the box became a folder, once: a run happens in a folder that exists, or not at all.</summary>
    [Fact]
    public void A_run_in_a_folder_that_is_not_there_is_refused_and_creates_nothing()
    {
        var missing = Path.Combine(_root, "typo");

        Assert.Contains("No such folder", RunLauncher.Refusal(missing, engineProblem: null));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void A_host_without_a_model_says_so_before_anything_starts()
        => Assert.Equal("No reasoner model is set.", RunLauncher.Refusal(_root, "No reasoner model is set."));

    [Fact]
    public void A_folder_that_exists_on_a_host_with_a_model_is_not_refused()
        => Assert.Null(RunLauncher.Refusal(_root, engineProblem: null));

    [Theory]
    [InlineData("fix the build", "fix the build")]
    [InlineData("first line\r\nsecond line", "first line  second line")]
    public void A_request_is_titled_by_its_first_words_on_one_line(string request, string title)
        => Assert.Equal(title, RunLauncher.Title(request));

    [Fact]
    public void A_long_request_is_titled_by_its_beginning()
        => Assert.Equal(new string('x', 70) + "…", RunLauncher.Title(new string('x', 200)));

    // ── runs in flight ──────────────────────────────────────────────────────

    /// <summary>
    /// A background run keeps going after the window switches folder, and the service shows each person the folder
    /// they have open: a run is listed under its own workspace, however that path was written, and nowhere else.
    /// </summary>
    [Fact]
    public void A_run_in_flight_is_listed_under_its_own_workspace_only()
    {
        var live = new LiveRuns();
        var here = Path.Combine(_root, "here");
        var row = live.Begin("digest", here, headless: true);
        live.Begin("elsewhere", Path.Combine(_root, "there"), headless: false);

        var listed = Assert.Single(live.In(" " + here + " "));
        Assert.Equal(row, listed.RowId);
        Assert.Equal("digest", listed.Title);
        Assert.Empty(live.In(Path.Combine(_root, "nowhere")));
    }

    /// <summary>An event that arrives after its run ended is not an error, and does not bring the row back.</summary>
    [Fact]
    public void A_row_changed_after_its_run_ended_stays_gone()
    {
        var live = new LiveRuns();
        var row = live.Begin("digest", _root, headless: false);

        Assert.True(live.Update(row, r => r with { StepsTotal = 3 }));
        Assert.Equal(3, Assert.Single(live.In(_root)).StepsTotal);

        Assert.True(live.End(row));
        Assert.False(live.Update(row, r => r with { StepsDone = 1 }));
        Assert.False(live.End(row));
        Assert.Empty(live.In(_root));
        Assert.Equal(0, live.Count);
    }

    // ── what is happening now ───────────────────────────────────────────────

    [Fact]
    public void The_coder_has_the_run_while_it_works_and_the_reasoner_while_it_reviews()
    {
        var activity = new RunActivity();

        Assert.Equal(RunActivity.Change.Agent | RunActivity.Change.Action, activity.Apply(Ev(EventKind.StepStarted, "step 1: write it")));
        Assert.Equal((AgentKind.Coder, "Coder", true), (activity.Agent, activity.AgentLabel, activity.AgentVisible));
        Assert.Equal("step 1: write it", activity.CurrentAction);

        activity.Apply(Ev(EventKind.ReviewRequested, "reviewing step 1"));
        Assert.Equal((AgentKind.Reasoner, "Reasoner · review"), (activity.Agent, activity.AgentLabel));
        Assert.Equal("reviewing step 1", activity.CurrentAction);
    }

    /// <summary>
    /// The reply arrives one event per token. A pill redrawn for each of them flickers; the hands changed once.
    /// </summary>
    [Fact]
    public void Tokens_from_the_same_hands_change_nothing()
    {
        var activity = new RunActivity();
        activity.Apply(Ev(EventKind.StepStarted, "step 1"));

        Assert.Equal(RunActivity.Change.None, activity.Apply(Ev(EventKind.AssistantDelta, "a")));
        Assert.Equal(RunActivity.Change.None, activity.Apply(Ev(EventKind.AssistantDelta, "b")));
        Assert.Equal("step 1", activity.CurrentAction);
    }

    [Fact]
    public void Each_tool_call_is_counted_and_shown_as_what_is_happening()
    {
        var activity = new RunActivity();

        activity.Apply(Ev(EventKind.ToolInvoked, "read_file a.md"));
        var changed = activity.Apply(Ev(EventKind.ToolInvoked, "write_file b.md"));

        Assert.Equal(RunActivity.Change.ToolCalls | RunActivity.Change.Action, changed);
        Assert.Equal(2, activity.ToolCalls);
        Assert.Equal("write_file b.md", activity.CurrentAction);
    }

    /// <summary>Why a run stopped belongs on screen, not only in the log - and nobody is working on it any more.</summary>
    [Fact]
    public void A_run_that_ended_says_why_and_shows_nobody_at_work()
    {
        var activity = new RunActivity();
        activity.Apply(Ev(EventKind.StepStarted, "step 1"));

        var changed = activity.Apply(Ev(EventKind.TaskFailed, "Incomplete: 1 step(s) did not finish",
            WorkEventPayload.OutcomePayload(RunOutcomeKind.Incomplete, "1 step(s) did not finish")));

        Assert.True(changed.HasFlag(RunActivity.Change.Ended));
        Assert.False(activity.AgentVisible);
        Assert.Equal("1 step(s) did not finish", activity.CurrentAction);
    }

    /// <summary>"Done" needs no words; a completed run says something only when a check it ran has to be added.</summary>
    [Fact]
    public void A_completed_run_says_nothing_unless_there_is_something_to_add()
    {
        var plain = new RunActivity();
        plain.Apply(Ev(EventKind.TaskCompleted, "Completed"));
        Assert.Equal(string.Empty, plain.CurrentAction);

        var withReason = new RunActivity();
        withReason.Apply(Ev(EventKind.TaskCompleted, "Completed",
            WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed, "the build check still fails")));
        Assert.Equal("the build check still fails", withReason.CurrentAction);
    }

    // ── what stopping would cut short ───────────────────────────────────────

    [Fact]
    public void Nothing_in_flight_is_nothing_to_ask_about()
        => Assert.False(new WorkInFlight(false, false, 0, 0).Any);

    /// <summary>The person stopping the host is not the person watching a phone's run: those are named first.</summary>
    [Theory]
    [InlineData(true, true, 2, 1, "waiting for your decision")]
    [InlineData(true, false, 2, 1, "started from your phone is still going")]
    [InlineData(false, false, 0, 3, "3 tasks started from your phone")]
    [InlineData(true, false, 2, 0, "A run is going, and 2 more in the background")]
    [InlineData(true, false, 0, 0, "A run is going. Closing")]
    [InlineData(false, false, 1, 0, "A background run is still going")]
    [InlineData(false, false, 4, 0, "4 background runs are still going")]
    public void Stopping_says_what_it_would_cut_short(bool foreground, bool decision, int background, int remote, string said)
    {
        var inFlight = new WorkInFlight(foreground, decision, background, remote);

        Assert.True(inFlight.Any);
        Assert.Contains(said, inFlight.Describe());
    }
}
