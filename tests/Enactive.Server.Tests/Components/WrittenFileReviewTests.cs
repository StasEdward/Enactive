namespace Enactive.Server.Tests.Components;

using System.Text;
using Bunit;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Server.Components.Runs;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Review on a file a run wrote shows what the run changed, as a diff against how the file was before the run - the copy
/// the run's store kept (WrittenFileReview) - with the whole file a switch away; and, where no such copy says how it was,
/// the file as it is with why there is no diff, never a diff against a guess. A Viewer may review.
/// </summary>
public sealed class WrittenFileReviewTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-review", Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public WrittenFileReviewTests()
    {
        Services.AddSingleton(new WorkspaceRoots([_folders]));
        _root = Path.Combine(_folders, "project");
        Directory.CreateDirectory(_root);
        Services.GetRequiredService<ServerEngine>().Workspaces.Touch(_root);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Enactive.Tests.SqlitePools.ClearUnder(_folders);
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
        }
    }

    /// <summary>
    /// A run, as the engine's store does it: notes.md as found then changed, new.md made. Written directly beside it -
    /// as a command would - plain.md, of which the run's record knows nothing.
    /// </summary>
    private async Task<Guid> RunAsync()
    {
        File.WriteAllText(Path.Combine(_root, "notes.md"), "line one\nline two\nline three\n");
        var runId = Guid.NewGuid();
        var store = new DiskArtifactStore(WorkspaceInfo.For(_root));
        store.BeginRun(runId, null);
        var step = store.BeginStep();
        await Write(step, "notes.md", "line one\nline 2\nline three\n");
        await Write(step, "new.md", "made by the run\n");
        File.WriteAllText(Path.Combine(_root, "plain.md"), "written by a command\n");
        return runId;
    }

    private static Task Write(IArtifactScope store, string path, string text)
        => store.CreateAsync(path, ArtifactKind.FileSet, path, s => s.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask(), default);

    private AngleSharp.Dom.IElement Reviewed(Guid runId, string path)
    {
        SignIn("vera");
        var files = Render<WrittenFiles>(p => p.Add(f => f.Root, _root).Add(f => f.RunId, runId).Add(f => f.Paths, ["notes.md", "new.md", "plain.md"]));
        files.FindAll(".artifact").Single(a => a.QuerySelector(".path")!.TextContent.Trim() == path)
            .QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Review").Click();
        return files.WaitForElement("[role=dialog]");
    }

    private static string[] Lines(AngleSharp.Dom.IElement dialog)
        => dialog.QuerySelectorAll(".diff-line").Select(l => l.TextContent).ToArray();

    [Fact]
    public async Task A_changed_file_shows_what_the_run_changed_against_how_it_was()
    {
        var run = await RunAsync();

        var dialog = Reviewed(run, "notes.md");

        Assert.Contains("against the file as the run found it", dialog.QuerySelector(".viewer-lead")!.TextContent);
        Assert.Equal(["# modified: notes.md", "", "  line one", "- line two", "+ line 2", "  line three", "  "], Lines(dialog));
        // The numbers in the file as it was and as it is, beside each line: line two was 2, line 2 is 2.
        var removed = dialog.QuerySelectorAll(".diff-row").Single(r => r.TextContent.Contains("- line two"));
        Assert.Equal(["2", ""], removed.QuerySelectorAll(".diff-num").Select(n => n.TextContent));
        // The whole file, one switch away.
        var whole = dialog.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Whole file");
        Assert.Equal("false", whole.GetAttribute("aria-pressed"));
        whole.Click();
        Assert.Equal("line one\nline 2\nline three\n", dialog.QuerySelector("pre.viewer-text")!.TextContent);
    }

    [Fact]
    public async Task A_new_file_is_all_the_run_s()
    {
        var run = await RunAsync();

        var dialog = Reviewed(run, "new.md");

        Assert.Contains("A new file", dialog.QuerySelector(".viewer-lead")!.TextContent);
        Assert.Equal(["# new file: new.md", "", "+ made by the run", "+ "], Lines(dialog));
    }

    /// <summary>
    /// No copy says how plain.md was - a command wrote it - so no diff: the file as it is, and why. A diff against "as it
    /// is now" or "empty" would show the run's work where there may be none.
    /// </summary>
    [Fact]
    public async Task A_file_the_run_kept_no_copy_of_is_shown_as_it_is_with_why()
    {
        var run = await RunAsync();

        var dialog = Reviewed(run, "plain.md");

        Assert.Empty(dialog.QuerySelectorAll(".diff"));
        Assert.Contains("No copy of how this file was before the run is kept", dialog.QuerySelector(".viewer-lead")!.TextContent);
        Assert.Equal("written by a command\n", dialog.QuerySelector("pre.viewer-text")!.TextContent);
    }

    /// <summary>Edited after the run: the run's changes cannot be told from the edit, so no diff that would credit it.</summary>
    [Fact]
    public async Task A_file_changed_after_the_run_is_shown_as_it_is_with_why()
    {
        var run = await RunAsync();
        File.WriteAllText(Path.Combine(_root, "notes.md"), "a person's edit, after the run\n");

        var dialog = Reviewed(run, "notes.md");

        Assert.Empty(dialog.QuerySelectorAll(".diff"));
        Assert.Contains("changed after the run", dialog.QuerySelector(".viewer-lead")!.TextContent);
        Assert.Equal("a person's edit, after the run\n", dialog.QuerySelector("pre.viewer-text")!.TextContent);
    }
}
