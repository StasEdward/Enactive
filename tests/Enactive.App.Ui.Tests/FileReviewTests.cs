namespace Enactive.App.Ui.Tests;

using System.Text;
using Enactive.App.Ui.ViewModels;
using Enactive.Application;
using Enactive.Application.Projections;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Workspace;

/// <summary>
/// The window's Review on a file a run wrote shows what the run changed - a diff against how the file was before the run,
/// the copy the run's store kept (WrittenFileReview, the service's review too) - each line with its numbers in the file as
/// it was and as it is, the whole file a switch away; and, where no copy says how it was, the file as it is with why.
/// </summary>
public sealed class FileReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-ui-review", Guid.NewGuid().ToString("N"));

    public FileReviewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>A run, as the engine's store does it: notes.md as found then changed; plain.md written beside it, as a command would.</summary>
    private async Task<Guid> RunAsync()
    {
        File.WriteAllText(Path.Combine(_root, "notes.md"), "line one\nline two\nline three\n");
        var runId = Guid.NewGuid();
        var store = new DiskArtifactStore(WorkspaceInfo.For(_root));
        store.BeginRun(runId, null);
        var step = store.BeginStep();
        await step.CreateAsync("notes.md", ArtifactKind.FileSet, "notes.md",
            s => s.WriteAsync(Encoding.UTF8.GetBytes("line one\nline 2\nline three\n")).AsTask(), default);
        File.WriteAllText(Path.Combine(_root, "plain.md"), "written by a command\n");
        return runId;
    }

    private async Task<FileReviewViewModel> ReviewedAsync(Guid runId, string path)
        => new(await WrittenFileReview.OfAsync(_root, runId, path, CancellationToken.None));

    [Fact]
    public async Task A_changed_file_is_a_numbered_diff_against_how_it_was()
    {
        var review = await ReviewedAsync(await RunAsync(), "notes.md");

        Assert.True(review.HasDiff);
        Assert.Contains("against the file as the run found it", review.Note);
        Assert.Equal(["# modified: notes.md", "", "  line one", "- line two", "+ line 2", "  line three", "  "],
            review.Rows.Select(r => r.Text));
        // Each line's numbers in the file as it was and as it is: line two was 2, line 2 is 2, line three is 3 in both.
        Assert.Equal(("2", ""), Row(review, "- line two"));
        Assert.Equal(("", "2"), Row(review, "+ line 2"));
        Assert.Equal(("3", "3"), Row(review, "  line three"));
        Assert.Equal(("", ""), Row(review, "# modified: notes.md"));
        Assert.Equal(DiffLineKind.Removed, review.Rows.Single(r => r.Text == "- line two").Kind);
        Assert.Equal("1 line added · 1 removed", review.Measured);
        // The whole file, what the switch shows and Copy all hands over.
        Assert.Equal("line one\nline 2\nline three\n", review.Text);
    }

    /// <summary>No copy says how plain.md was - a command wrote it - so no diff: the file as it is, and why.</summary>
    [Fact]
    public async Task A_file_the_run_kept_no_copy_of_is_the_file_as_it_is_with_why()
    {
        var review = await ReviewedAsync(await RunAsync(), "plain.md");

        Assert.False(review.HasDiff);
        Assert.Contains("No copy of how this file was before the run is kept", review.Note);
        Assert.Equal("written by a command\n", review.Text);
    }

    private static (string Old, string New) Row(FileReviewViewModel review, string text)
        => review.Rows.Single(r => r.Text == text) is var row ? (row.Old, row.New) : default;
}
