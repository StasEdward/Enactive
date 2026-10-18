namespace Enactive.App.Ui.Tests;

using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Enactive.App.Ui.ViewModels;
using Enactive.Application.Projections;
using Enactive.Core.Checks;
using Enactive.Core.History;

/// <summary>
/// "Run checks now" in the window (Plan Phase 3.2, staged runs): on a staged run's page in the history and beside the live
/// run's proposals once it has ended - the same action and record list as the service's pages. The window has one person,
/// so no role is asked; it follows its own staged apply, which asks none either.
/// </summary>
public sealed class RunChecksNowTests
{
    private static RunRecord Run(bool staged, bool withChecks = true)
    {
        var at = DateTimeOffset.Now.AddMinutes(-5);
        var notes = new CheckDefinition("notes", null, "echo ran", [], CheckFire.Run, CheckSeverity.Advisory, 30, null);
        return new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "write the notes", null, at, at.AddSeconds(5), "Incomplete", [], ["notes.md"], [],
            new RunSettings(3, "autonomous", null, staged))
        {
            CheckSnapshot = withChecks ? new CheckSnapshot([new ResolvedCheck(notes, CheckOrigin.Workspace)], []) : null
        };
    }

    private static PastRunViewModel Past(RunRecord record) => new(record, _ => { }, _ => { });

    [Fact]
    public void A_staged_runs_page_offers_it_and_asks_the_window_to_run_that_runs_checks()
    {
        var past = Past(Run(staged: true));
        PastRunViewModel? asked = null;
        past.Checks.RunRequested += _ => asked = past;

        Assert.True(past.Checks.Offered);
        Assert.Equal(CheckRecordViews.Explanation, past.Checks.Explanation);
        Assert.True(past.Checks.RunCommand.CanExecute(null));
        past.Checks.RunCommand.Execute(null);
        Assert.Same(past, asked);

        // While it runs, a second press is not a second set of commands.
        past.Checks.Busy = true;
        Assert.False(past.Checks.RunCommand.CanExecute(null));
        Assert.Equal("Running checks…", past.Checks.ButtonText);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_run_that_wrote_to_disk_or_had_no_checks_is_not_offered_it(bool staged, bool withChecks)
    {
        var past = Past(Run(staged, withChecks));

        Assert.False(past.Checks.Offered);
        Assert.False(past.Checks.RunCommand.CanExecute(null));
        Assert.False(past.Checks.IsShown);
    }

    /// <summary>Each record says when it was checked and whether the workspace still matches it; never a green.</summary>
    [Fact]
    public void Records_are_listed_newest_first_as_checked_or_outdated()
    {
        var checks = new CheckRecordsViewModel(offered: true);
        var line = new CheckResultLine("notes", Enactive.Core.Templates.CriterionOutcome.Passed, false, "echo ran", 0, null);
        var fingerprint = new StateFingerprint(FingerprintKind.Git, new string('a', 64));
        var newer = CheckRecordViews.Of(new CheckRecord(Guid.NewGuid(), Guid.NewGuid(), "me", DateTimeOffset.Now, [], [line], fingerprint), current: true);
        var older = CheckRecordViews.Of(new CheckRecord(Guid.NewGuid(), Guid.NewGuid(), "me", DateTimeOffset.Now.AddHours(-1), [], [line], fingerprint), current: false);

        checks.Show([newer, older]);

        Assert.True(checks.IsShown);
        Assert.Equal([newer.Headline, older.Headline], checks.Records.Select(r => r.Headline));
        Assert.StartsWith("Checked at ", checks.Records[0].State);
        Assert.StartsWith("Outdated", checks.Records[1].State);
        Assert.Equal([false, true], checks.Records.Select(r => r.Outdated));
        Assert.Equal("PASS notes (echo ran)", Assert.Single(checks.Records[0].Lines).Text);
    }

    /// <summary>
    /// The window places it where it shows a staged run's proposals - the live run's Artifacts, once the run has ended - and
    /// on a past run's page, both through the one view bound to the action and its records.
    /// </summary>
    [Fact]
    public void The_window_shows_it_on_a_past_run_and_beside_the_live_runs_proposals()
    {
        var window = XDocument.Load(Path.Combine(Root(), "src", "Enactive.App.Ui", "MainWindow.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => window.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);

        var past = Named("PastRunChecks");
        var live = Named("LiveRunChecks");
        Assert.Equal(("CheckRecordsView", "{Binding PastRun.Checks}"), (past.Name.LocalName, (string?)past.Attribute("DataContext")));
        Assert.Equal(("CheckRecordsView", "{Binding LiveChecks}"), (live.Name.LocalName, (string?)live.Attribute("DataContext")));
        // The live one only once a staged run has ended; it sits with the run's proposals, in its Artifacts tab.
        Assert.Equal("{Binding HasLiveChecks}", (string?)live.Parent!.Attribute("IsVisible"));
        Assert.Contains(live.Ancestors(), a => (string?)a.Attribute("IsVisible") == "{Binding IsArtifactsTab}");

        var view = XDocument.Load(Path.Combine(Root(), "src", "Enactive.App.Ui", "CheckRecordsView.axaml"));
        Assert.Equal("{Binding IsShown}", (string?)view.Root!.Elements().First().Attribute("IsVisible"));
        var button = view.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "RunChecksNow");
        Assert.Equal(("{Binding RunCommand}", "{Binding ButtonText}", "{Binding Offered}"),
            ((string?)button.Attribute("Command"), (string?)button.Attribute("Content"), (string?)button.Attribute("IsVisible")));
    }

    /// <summary>
    /// The checks of a run in another folder than the one on screen run at THAT folder's level, not the slider's - the
    /// slider is about the folder on screen; the folder on screen uses the slider, as a run started from it does.
    /// </summary>
    [Fact]
    public void Checks_run_at_the_level_of_the_folder_the_run_was_in()
    {
        var shown = Path.Combine(Path.GetTempPath(), "shown");
        var other = Path.Combine(Path.GetTempPath(), "other");
        int? Saved(string folder) => folder == other ? 0 : null;

        Assert.Equal(3, MainWindow.ChecksAutonomy(shown, Enactive.Workspace.WorkspaceRegistry.Normalise(shown), 3, Saved));
        Assert.Equal(0, MainWindow.ChecksAutonomy(other, Enactive.Workspace.WorkspaceRegistry.Normalise(shown), 3, Saved));
        Assert.Equal(3, MainWindow.ChecksAutonomy(Path.Combine(Path.GetTempPath(), "unknown"), shown, 3, Saved));

        // The folder on screen, however it is written: a trailing separator, and - where the file system ignores case -
        // another case. Not the saved level of a folder that only looks different.
        int? Never(string _) => 0;
        Assert.Equal(3, MainWindow.ChecksAutonomy(shown + Path.DirectorySeparatorChar, shown, 3, Never));
        var ignoresCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(ignoresCase ? 3 : 0, MainWindow.ChecksAutonomy(shown.ToUpperInvariant(), shown, 3, Never));
    }

    /// <summary>
    /// The live run's action is for the folder the run ran in, kept when it started - not the folder on screen when it ends,
    /// which a person may have switched in the meantime.
    /// </summary>
    [Fact]
    public void The_live_action_is_for_the_folder_the_run_ran_in()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "src", "Enactive.App.Ui", "MainWindow.axaml.cs"));

        Assert.Contains("_stagedRunRoot = fullPath;", code);
        Assert.Contains("_stagedRunRoot is { Length: > 0 } stagedRoot", code);
        Assert.Contains("_ = ShowLiveChecksAsync(stagedRoot, stagedRun);", code);
        Assert.DoesNotContain("_currentWorkspaceRoot is { Length: > 0 } stagedRoot", code);
    }

    private static string Root([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
