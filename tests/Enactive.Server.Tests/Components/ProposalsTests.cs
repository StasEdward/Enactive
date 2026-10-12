namespace Enactive.Server.Tests.Components;

using System.Text;
using Bunit;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Server.Accounts;
using Enactive.Server.Components.Pages;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The proposed changes as the shell shows them: what waits, in every workspace the service knows (/proposals), and one
/// run's set with its diffs (/proposals/{workspace}/{set}) - the workspace by its id, never its path. A change is applied or
/// rejected by an Operator once the run has ended; a Viewer reads. Every decision is ServerStaging's (and checked again
/// there), and the diffs and the buttons are the ones the Runs screen's Artifacts tab draws.
/// </summary>
public sealed class ProposalsTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-proposals", Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public ProposalsTests()
    {
        _root = Path.Combine(_folders, "project");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "notes.md"), "old\n");
        File.WriteAllText(Path.Combine(_root, "todo.md"), "one\n");
        Services.GetRequiredService<ServerEngine>().Workspaces.Touch(_root);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
    }

    private ServerStaging Staging => Services.GetRequiredService<ServerStaging>();

    private Guid WorkspaceId => WorkspaceInfo.For(_root).Id;

    /// <summary>A run that proposed new notes.md and todo.md; ended unless it is still being written. Its set's name.</summary>
    private async Task<string> StagedAsync(bool ended = true)
    {
        var run = Guid.NewGuid();
        var folder = Staging.Begin(_root, run, "alice", "tidy the notes");
        using var store = new StagingArtifactStore(_root, folder);
        await store.CreateAsync("notes.md", ArtifactKind.FileSet, "notes", s => s.WriteAsync(Encoding.UTF8.GetBytes("new\n")).AsTask(), CancellationToken.None);
        await store.CreateAsync("todo.md", ArtifactKind.FileSet, "todo", s => s.WriteAsync(Encoding.UTF8.GetBytes("one\ntwo\n")).AsTask(), CancellationToken.None);
        if (ended)
            await Staging.EndAsync(_root, folder, "tidy the notes", run, store);
        return run.ToString("N");
    }

    private IRenderedComponent<ProposalsScreen> OpenSet(string who, string set)
    {
        SignIn(who);
        var page = Render<ProposalsScreen>(p => p.Add(s => s.WorkspaceId, WorkspaceId).Add(s => s.Set, set));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".proposal").Count));
        return page;
    }

    private static string[] Buttons(IRenderedComponent<ProposalsScreen> page)
        => page.FindAll(".proposal button").Select(b => b.TextContent.Trim()).ToArray();

    private static AngleSharp.Dom.IElement Card(IRenderedComponent<ProposalsScreen> page, string path)
        => page.FindAll(".proposal").Single(c => c.QuerySelector(".path")!.TextContent.Trim() == path);

    /// <summary>
    /// The set's diffs are open when it is, each in the colours of a diff; Apply writes one file and leaves the other
    /// waiting, and what the service said is under the heading. The same Apply the Runs screen's Artifacts tab has.
    /// </summary>
    [Fact]
    public async Task An_operator_applies_one_proposal_and_it_is_written()
    {
        var set = await StagedAsync();
        var page = OpenSet("alice", set);

        Assert.Equal("tidy the notes", page.Find("h1").TextContent.Trim());
        Assert.Contains(Card(page, "notes.md").QuerySelectorAll(".diff-line"), l => l.TextContent == "+ new" && l.ClassList.Contains("tone-done"));
        Assert.Contains("2 of 2 waiting", page.Find(".proposals-meta").TextContent);

        Card(page, "notes.md").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Apply").Click();

        page.WaitForAssertion(() => Assert.StartsWith("Applied: notes.md.", page.Find(".said").TextContent.Trim()));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(_root, "notes.md")));
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(_root, "todo.md")));
        // It stays on the page as what was done, and the other change still waits.
        Assert.Equal("applied", Card(page, "notes.md").QuerySelector(".proposal-state")!.TextContent.Trim());
        Assert.Contains("tone-done", Card(page, "notes.md").ClassList);
        Assert.Equal("staged", Card(page, "todo.md").QuerySelector(".proposal-state")!.TextContent.Trim());
        Assert.Contains("1 of 2 waiting", page.Find(".proposals-meta").TextContent);
        Assert.DoesNotContain(Card(page, "notes.md").QuerySelectorAll("button"), b => b.TextContent.Trim() == "Apply");
    }

    /// <summary>Reject leaves the file as it was, and the change is drawn as rejected.</summary>
    [Fact]
    public async Task An_operator_rejects_one_proposal_and_the_file_is_left_alone()
    {
        var set = await StagedAsync();
        var page = OpenSet("alice", set);

        Card(page, "todo.md").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Reject").Click();

        page.WaitForAssertion(() => Assert.StartsWith("Rejected: todo.md.", page.Find(".said").TextContent.Trim()));
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(_root, "todo.md")));
        Assert.Contains("tone-failed", Card(page, "todo.md").ClassList);
    }

    /// <summary>
    /// The last decision drops the set from the service; the page keeps what was decided in front of the person who made it
    /// rather than going empty under their eyes - and says every proposal is decided.
    /// </summary>
    [Fact]
    public async Task Deciding_the_last_proposal_keeps_the_set_on_the_page()
    {
        var set = await StagedAsync();
        var page = OpenSet("alice", set);

        foreach (var path in new[] { "notes.md", "todo.md" })
        {
            Card(page, path).QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Apply").Click();
            page.WaitForAssertion(() => Assert.Contains(path, page.Find(".said").TextContent));
        }

        Assert.EndsWith("Every proposal of this run is decided.", page.Find(".said").TextContent.Trim());
        Assert.Equal(2, page.FindAll(".proposal").Count);
        Assert.All(page.FindAll(".proposal-state"), s => Assert.Equal("applied", s.TextContent.Trim()));
        Assert.Contains("0 of 2 waiting", page.Find(".proposals-meta").TextContent);
    }

    /// <summary>
    /// A run still being written has its proposals shown, and nothing to decide: the store is still being written, and the
    /// service refuses then anyway.
    /// </summary>
    [Fact]
    public async Task A_set_still_being_written_offers_nothing_to_do()
    {
        var set = await StagedAsync(ended: false);

        var page = OpenSet("alice", set);

        Assert.Contains("The run is still going", page.Markup);
        Assert.Equal(["Diff", "Diff"], Buttons(page));
        Assert.NotEmpty(page.FindAll(".diff-line"));
    }

    /// <summary>A Viewer reads the diff and is offered neither Apply nor Reject; the file is untouched.</summary>
    [Fact]
    public async Task A_viewer_reads_the_diff_and_is_offered_nothing()
    {
        var set = await StagedAsync();

        var page = OpenSet("vera", set);

        Assert.Contains(Card(page, "notes.md").QuerySelectorAll(".diff-line"), l => l.TextContent == "+ new");
        Assert.Equal(["Diff", "Diff"], Buttons(page));
        Assert.Equal("old\n", File.ReadAllText(Path.Combine(_root, "notes.md")));
    }

    /// <summary>A set that is not kept (decided, dropped, or no workspace the service knows) says so, and offers nothing.</summary>
    [Fact]
    public void A_set_that_is_not_kept_says_so()
    {
        SignIn("alice");

        var gone = Render<ProposalsScreen>(p => p.Add(s => s.WorkspaceId, WorkspaceId).Add(s => s.Set, Guid.NewGuid().ToString("N")));
        gone.WaitForAssertion(() => Assert.Contains("no longer kept", gone.Markup));
        Assert.Empty(gone.FindAll(".proposal"));

        // A workspace id the service does not know is not looked up anywhere else.
        var unknown = Render<ProposalsScreen>(p => p.Add(s => s.WorkspaceId, Guid.NewGuid()).Add(s => s.Set, Guid.NewGuid().ToString("N")));
        unknown.WaitForAssertion(() => Assert.Contains("no longer kept", unknown.Markup));
    }

    /// <summary>
    /// What waits in every workspace, each a link to its set by the workspace's id; running sets say so; nothing waiting
    /// says that.
    /// </summary>
    [Fact]
    public async Task The_list_links_each_waiting_set_by_the_workspace_s_id()
    {
        SignIn("vera");
        var empty = Render<ProposalsScreen>();
        empty.WaitForAssertion(() => Assert.Contains("Nothing waits.", empty.Markup));

        var set = await StagedAsync();
        var page = Render<ProposalsScreen>();

        page.WaitForAssertion(() => Assert.Single(page.FindAll(".proposal-set")));
        var row = page.Find(".proposal-set");
        Assert.Equal($"proposals/{WorkspaceId}/{set}", row.GetAttribute("href"));
        Assert.Contains("tidy the notes", row.TextContent);
        Assert.Contains("project", row.TextContent);
        Assert.Contains("alice", row.TextContent);
        Assert.Contains("2 of 2 waiting", row.TextContent);
        Assert.Contains("tone-open", row.ClassList);
        // Nobody's path is in the address.
        Assert.DoesNotContain(_folders, row.GetAttribute("href"));
    }

    [Fact]
    public async Task A_set_still_being_written_is_listed_as_running()
    {
        await StagedAsync(ended: false);
        SignIn("vera");

        var page = Render<ProposalsScreen>();

        page.WaitForAssertion(() => Assert.Contains("the run is still going", page.Find(".proposal-set").TextContent));
    }

    /// <summary>
    /// The list is read again when somebody decides a change: a set whose last proposal was applied by another person is
    /// not left on the page of one who had it open.
    /// </summary>
    [Fact]
    public async Task The_list_follows_what_is_decided_elsewhere()
    {
        var set = await StagedAsync();
        SignIn("alice");
        var page = Render<ProposalsScreen>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".proposal-set")));

        var alice = ServerAuth.PrincipalOf(Users.Find("alice")!);
        foreach (var file in Staging.Open(alice, _root, set).Files)
            await Staging.ActAsync(alice, _root, set, file.Id, apply: false, CancellationToken.None);

        page.WaitForAssertion(() => Assert.Contains("Nothing waits.", page.Markup));
    }
}
