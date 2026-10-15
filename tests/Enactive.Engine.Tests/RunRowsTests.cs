namespace Enactive.Engine.Tests;

using Enactive.Application.Projections;
using Enactive.Core.History;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The words of the runs column and the workspace switcher, which the window and the service's Runs screen both say
/// (RunRows, WorkspaceRows): one place, so the two cannot drift apart.
/// </summary>
public sealed class RunRowsTests
{
    /// <summary>
    /// The names are indexed by the filter: a filter added without its name was an index past the list - the window's
    /// combo box and the Runs screen's select both fell over on it.
    /// </summary>
    [Fact]
    public void Every_filter_has_its_name_in_its_order()
    {
        Assert.Equal(Enum.GetValues<RunFilter>().Length, RunRows.FilterNames.Count);
        Assert.Equal("All runs", RunRows.FilterNames[(int)RunFilter.All]);
        Assert.Equal("Older than a week", RunRows.FilterNames[(int)RunFilter.OlderThanAWeek]);
    }

    [Fact]
    public void The_delete_button_says_how_many_it_takes()
        => Assert.Equal("Delete these 3", RunRows.DeleteShown(3));

    /// <summary>
    /// The RUNS line, which the window's column and the service's said separately: none, a count, and both numbers under a
    /// filter - "3 runs" over a list of 37 reads as the whole truth.
    /// </summary>
    [Fact]
    public void The_runs_line_says_how_many_are_shown_of_how_many()
    {
        Assert.Equal("No runs recorded in this workspace.", RunRows.Status(0, 0));
        Assert.Equal("1 run", RunRows.Status(1, 1));
        Assert.Equal("37 runs", RunRows.Status(37, 37));
        Assert.Equal("3 of 37 runs", RunRows.Status(3, 37));
    }

    /// <summary>A folder that is gone is red even where you are: being gone is the thing to see.</summary>
    [Theory]
    // By name: WorkspacePlace is internal, and a public test method cannot take it.
    [InlineData(true, true, "Current")]
    [InlineData(true, false, "Other")]
    [InlineData(false, true, "Missing")]
    [InlineData(false, false, "Missing")]
    public void A_missing_folder_outranks_being_current(bool exists, bool isCurrent, string place)
        => Assert.Equal(Enum.Parse<WorkspacePlace>(place), WorkspaceRows.Place(exists, isCurrent));

    [Fact]
    public void A_missing_folder_is_marked_not_hidden()
    {
        var entry = new WorkspaceEntry("notes", @"D:\work\notes", DateTimeOffset.UnixEpoch);

        Assert.Equal(@"D:\work\notes", WorkspaceRows.Detail(entry, exists: true));
        Assert.Equal(@"Folder not found · D:\work\notes", WorkspaceRows.Detail(entry, exists: false));
    }

    /// <summary>
    /// Forgetting a workspace asks first, in words that say what is lost and what is not - the window's, which the service's
    /// switcher asks too.
    /// </summary>
    [Fact]
    public void Forgetting_a_workspace_says_what_goes_and_what_stays()
    {
        var question = WorkspaceRows.Forget("notes");

        Assert.Equal("Forget “notes”?", question.Headline);
        Assert.Equal("The folder and everything in it, including its run history, are left exactly where they are. "
                     + "What goes is the name you gave it here and how runs are set up in it.", question.Body);
        Assert.Equal(("Forget", "Keep"), (question.Confirm, question.Cancel));
    }
}
