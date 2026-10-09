namespace Enactive.App.Ui.Tests;

using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Enactive.App.Ui.ViewModels;
using Enactive.Core.History;

/// <summary>
/// An UNFINISHED row: resumed or deleted, both by an icon as on the history rows. Without the delete, a run that would
/// never be resumed could only be resumed - nine of them stood above the history for good.
/// </summary>
public sealed class UnfinishedRunTests
{
    private static RunCheckpoint Checkpoint()
    {
        var first = Guid.NewGuid();
        return new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now.AddMinutes(-10), DateTimeOffset.Now,
            "Add tests", "Add missing coverage tests", "developer", Spec: null,
            Steps:
            [
                new CheckpointStep(first, "first", [], "Normal", "Done", "Succeeded"),
                new CheckpointStep(Guid.NewGuid(), "second", [first], "Normal", "Pending"),
                new CheckpointStep(Guid.NewGuid(), "third", [first], "Normal", "Running")
            ],
            Digest: [], Transcript: [], Artifacts: [], StepsRun: 1, TokensSpent: 0);
    }

    [Fact]
    public void Its_delete_asks_the_window_to_forget_that_run()
    {
        var runs = new RunsViewModel();
        var checkpoint = Checkpoint();
        RunCheckpoint? forgotten = null;
        runs.ForgetUnfinishedRequested += c => forgotten = c;
        runs.ShowResumable([checkpoint]);

        var row = Assert.Single(runs.Resumable);
        Assert.True(row.RemoveCommand.CanExecute(null));
        row.RemoveCommand.Execute(null);

        Assert.Same(checkpoint, forgotten);
    }

    /// <summary>An icon says nothing by itself: its hint says what pressing it runs.</summary>
    [Fact]
    public void Its_resume_says_what_is_left()
        => Assert.Equal("Resume: run the 2 step(s) that are left", new ResumableRunViewModel(Checkpoint(), _ => { }).ResumeTip);

    /// <summary>Both are icons with hints in the row, bound to the row's commands - not a word button.</summary>
    [Fact]
    public void The_row_has_both_icons()
    {
        var window = XDocument.Load(Path.Combine(Root(), "src", "Enactive.App.Ui", "MainWindow.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => window.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);

        var resume = Named("ResumeUnfinished");
        var forget = Named("ForgetUnfinished");

        Assert.Equal(("{Binding ResumeCommand}", "{Binding ResumeTip}"), ((string?)resume.Attribute("Command"), (string?)resume.Attribute("ToolTip.Tip")));
        Assert.Equal("{Binding RemoveCommand}", (string?)forget.Attribute("Command"));
        Assert.Contains("destructive", (string?)forget.Attribute("Classes"));
        Assert.Null(resume.Attribute("Content"));
    }

    /// <summary>From this file, not from the build output: the tests are built outside the repository while the app runs.</summary>
    private static string Root([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
