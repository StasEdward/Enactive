namespace Enactive.Engine.Tests;

using Xunit;

/// <summary>
/// Where the window and the console hand "Pause after this step" its run (Plan Phase 4.2), read from their source - each
/// is code in a window or in top-level statements no test can run, and any one line missing leaves a control that looks
/// right and does nothing: a pause never handed to the engine, a staged run offered a pause it ignores, a header that
/// never hears of the plan, or a greyed button left over a run that is over. What the lines call is tested elsewhere
/// (PauseOfferTests, PauseAfterThisStepTests, ConsolePauseKeyTests); this is that they are called, and where.
/// </summary>
public sealed class PauseWiringTests
{
    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([TestRepository.Root, .. path]));

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' is not in the source");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' does not follow '{from}'");
        return text[start..end];
    }

    private static string Window() => Read("src", "Enactive.App.Ui", "MainWindow.axaml.cs");

    private static string WindowRun() => Between(Window(), "private async Task RunAsync(", "private void RenderEvent(WorkEvent ev)");

    private static string ConsoleSource() => Read("src", "Enactive.App.Console", "Program.cs");

    // ── the window ──────────────────────────────────────────────────────────

    /// <summary>The run's own pause, from the slot that made it beside Stop's cancellation, to the header's control.</summary>
    [Fact]
    public void The_window_gives_the_header_the_run_s_own_pause()
        => Assert.Matches(@"var pause = _foreground\.Pause;\s+_vm\.Pause = new PauseOffer\(pause\);", WindowRun());

    /// <summary>The same pause goes to the engine with the request; without it the button asks a pause nobody reads.</summary>
    [Fact]
    public void The_window_hands_the_pause_in_with_the_run_s_request()
        => Assert.Matches(@"new RunRequest\([^;]*\{ Defaults = DefaultsOnScreen\(\), Pause = pause \}", WindowRun());

    /// <summary>A staged run ignores a pause: the header is told once the composition says the run stages.</summary>
    [Fact]
    public void The_window_tells_the_header_a_staged_run_cannot_pause()
    {
        var composed = Between(WindowRun(), "_staging = composed.Artifacts as StagingArtifactStore;", "await RunEventPump.RunAsync(");

        Assert.Contains("var staged = _staging is not null;", composed, StringComparison.Ordinal);
        Assert.Matches(@"if \(staged\)\s+\{\s+_vm\.Pause\?\.Staging\(\);\s+_vm\.PauseChanged\(\);", composed);
    }

    /// <summary>The plan, a quick action and the end reach the header's control as each event is drawn.</summary>
    [Fact]
    public void The_window_folds_every_event_it_draws_into_the_pause()
    {
        var render = Window()[Window().IndexOf("private void RenderEvent(WorkEvent ev)", StringComparison.Ordinal)..];

        Assert.Contains("_vm.ApplyToPause(ev);", render[..2000], StringComparison.Ordinal);
    }

    /// <summary>However the run ended - a cancel, an error, an end - the control goes with it.</summary>
    [Fact]
    public void The_window_takes_the_pause_away_when_the_run_is_over()
        => Assert.Contains("_vm.Pause = null;", Between(WindowRun(), "var finalElapsed", "EndLive(endedRow);"), StringComparison.Ordinal);

    // ── the console ─────────────────────────────────────────────────────────

    [Fact]
    public void The_console_hands_the_pause_in_with_the_run_s_request()
    {
        var program = ConsoleSource();

        Assert.Contains("var pause = new Enactive.Core.Execution.RunPause();", program, StringComparison.Ordinal);
        Assert.Contains("Pause = pause", Between(program, "new RunRequest(workspace, command,", "decisionHandler, cts.Token);"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_console_folds_the_run_s_events_and_its_staging_into_the_pause()
    {
        var program = ConsoleSource();

        Assert.Matches(@"if \(composed\.Artifacts is StagingArtifactStore\)\s+pauseOffer\.Staging\(\);", program);
        Assert.Matches(@"await foreach \(var ev in composed\.Events\(cts\.Token\)\)\s+\{\s+pauseOffer\.Apply\(ev\);", program);
        Assert.Matches(@"pauseOffer\.Ended\(\);\s+keysDone\.Cancel\(\);\s+await keys;", program);
    }

    /// <summary>P is read beside the run, never while the permission prompt reads its answer, and the banner names it.</summary>
    [Fact]
    public void The_console_reads_P_beside_the_run_and_leaves_the_prompt_its_keys()
    {
        var program = ConsoleSource();

        Assert.Contains("if (!ConsoleDecisionHandler.Asking && Console.KeyAvailable)", program, StringComparison.Ordinal);
        Assert.Contains("ConsolePauseKey.Press(Console.ReadKey(intercept: true).KeyChar, pauseOffer)", program, StringComparison.Ordinal);
        Assert.Matches(@"Asking = true;\s+try \{ return Ask\(request\); \}\s+finally \{ Asking = false; \}", program);
        Assert.Contains("{ConsolePauseKey.Banner}", program, StringComparison.Ordinal);
    }
}
