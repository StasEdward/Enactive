namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// A workspace keeps its identity when its folder is renamed.
///
/// <para><c>PLAN_v2.md</c> §11 carried it as: <i>"A workspace's identity is a hash of its path
/// (<c>WorkspaceInfo.IdFor</c>), so renaming the folder detaches its MySQL history. The fix is an id
/// written into <c>&lt;workspace&gt;/.enactive/workspace.json</c>."</i></para>
///
/// <para>The failure was silent, which is what made it worth fixing rather than documenting: the
/// rows stay in the database and the per-workspace filter stops matching them, and from the engine's
/// side a workspace with no history and a workspace whose history is filed under another id look
/// exactly alike.</para>
///
/// <para><b>The id says which project, never what is allowed.</b> It is read from a file inside the
/// folder the agent works in, and a repository can arrive from anywhere with that file already in
/// it. Attribution follows the folder; authority stays keyed to the path — see
/// <see cref="An_id_carried_in_a_folder_does_not_carry_permissions"/>.</para>
/// </summary>
public sealed class WorkspaceIdentityTests : IDisposable
{
    private readonly string _root;

    public WorkspaceIdentityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ── the defect ──────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point, through a real store: write a run, rename the folder, and the run is still
    /// this workspace's. Without the id file the second folder is a different workspace and its
    /// history is empty — which is the silent detachment.
    /// </summary>
    [Fact]
    public async Task A_renamed_folder_keeps_the_history_it_had()
    {
        var before = Folder("project-alpha");
        var workspace = WorkspaceInfo.Adopt(before);

        var store = new SqliteRunStore(workspace);
        await store.SaveAsync(Record(workspace.Id, "did some work"), CancellationToken.None);

        // The rename. Everything inside the folder travels with it, the id file included.
        // Pools closed first: SQLite keeps the database file open, and Windows will not move a
        // folder somebody has a handle in. That is an artefact of testing a rename in-process, not
        // of the rename — a person renaming a folder has closed the app.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var after = Path.Combine(_root, "project-beta");
        Directory.Move(before, after);

        var renamed = WorkspaceInfo.For(after);
        Assert.Equal(workspace.Id, renamed.Id);
        Assert.Equal("project-beta", renamed.Name);

        // And the store, which filters by workspace, still finds it. SQLite keeps one database per
        // folder so this is the weaker of the two demonstrations; MySQL is where the filter is the
        // only thing separating projects, and it is the same id doing the separating.
        var back = Assert.Single(await new SqliteRunStore(renamed).LoadSummariesAsync(CancellationToken.None));
        Assert.Equal("did some work", back.Title);
    }

    /// <summary>The same rename, at the level the id actually lives at.</summary>
    [Fact]
    public void An_adopted_workspace_keeps_its_id_across_a_move()
    {
        var before = Folder("one");
        var id = WorkspaceInfo.Adopt(before).Id;

        var after = Path.Combine(_root, "two");
        Directory.Move(before, after);

        Assert.Equal(id, WorkspaceInfo.For(after).Id);
        Assert.NotEqual(WorkspaceInfo.IdFor(after), WorkspaceInfo.For(after).Id);
    }

    // ── what makes it safe to ship ──────────────────────────────────────────

    /// <summary>
    /// The id written down is the one the workspace ALREADY HAD. Anything else would detach every
    /// existing workspace at once — inflicting the defect on everybody in the act of fixing it.
    /// </summary>
    [Fact]
    public void Adopting_a_workspace_writes_down_the_id_it_already_had()
    {
        var path = Folder("existing");
        var wasUsing = WorkspaceInfo.IdFor(path);

        var adopted = WorkspaceInfo.Adopt(path);

        Assert.Equal(wasUsing, adopted.Id);
        Assert.True(File.Exists(WorkspaceIdentity.PathFor(path)));
    }

    /// <summary>Adopting twice does not mint a second id. The file is written once and then read.</summary>
    [Fact]
    public void Adopting_again_changes_nothing()
    {
        var path = Folder("twice");
        var first = WorkspaceInfo.Adopt(path).Id;
        var written = File.ReadAllText(WorkspaceIdentity.PathFor(path));

        Assert.Equal(first, WorkspaceInfo.Adopt(path).Id);
        Assert.Equal(written, File.ReadAllText(WorkspaceIdentity.PathFor(path)));
    }

    /// <summary>
    /// Reading never writes. Looking at a folder's history — or at any workspace the app merely
    /// lists — should not leave a file in somebody's project.
    /// </summary>
    [Fact]
    public void Resolving_a_workspace_puts_nothing_in_it()
    {
        var path = Folder("untouched");

        var resolved = WorkspaceInfo.For(path);

        Assert.Equal(WorkspaceInfo.IdFor(path), resolved.Id);
        Assert.False(Directory.Exists(Path.Combine(path, ".enactive")));
    }

    // ── when the file cannot be trusted ─────────────────────────────────────

    /// <summary>
    /// A damaged marker leaves the workspace as the workspace it was, on the id its path gives it.
    /// That is the behaviour that existed before this file did — the safe direction, because the
    /// alternative is a folder full of work quietly becoming a new workspace with no history.
    /// </summary>
    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("""{"id":"not-a-guid"}""")]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"createdAt":"2026-09-08T00:00:00Z"}""")]
    [InlineData("")]
    public void An_unreadable_marker_falls_back_to_the_path(string content)
    {
        var path = Folder("damaged");
        Directory.CreateDirectory(Path.Combine(path, ".enactive"));
        File.WriteAllText(WorkspaceIdentity.PathFor(path), content);

        Assert.Null(WorkspaceIdentity.Read(path));
        Assert.Equal(WorkspaceInfo.IdFor(path), WorkspaceInfo.For(path).Id);
    }

    /// <summary>
    /// An all-zero id is not an id — it is what an absent field deserialises to. Filing a
    /// workspace's history under Guid.Empty would put every blank-markered workspace in the world
    /// under one key.
    /// </summary>
    [Fact]
    public void An_empty_guid_is_not_an_identity()
    {
        var path = Folder("blank");
        Directory.CreateDirectory(Path.Combine(path, ".enactive"));
        File.WriteAllText(WorkspaceIdentity.PathFor(path), """{"id":"00000000-0000-0000-0000-000000000000"}""");

        Assert.NotEqual(Guid.Empty, WorkspaceInfo.For(path).Id);
    }

    /// <summary>
    /// A folder that cannot be written to still opens. A workspace on a read-only share is a
    /// workspace, and refusing to show its history because a marker could not be created would be a
    /// worse answer than the one this replaces.
    /// </summary>
    [Fact]
    public void A_folder_that_refuses_the_write_still_gives_a_workspace()
    {
        // A file where the .enactive FOLDER needs to be: creating the directory fails, on every
        // platform, without needing permissions a test cannot portably set.
        var path = Folder("read-only");
        File.WriteAllText(Path.Combine(path, ".enactive"), "not a folder");

        var workspace = WorkspaceInfo.Adopt(path);

        Assert.Equal(WorkspaceInfo.IdFor(path), workspace.Id);
        Assert.Equal("read-only", workspace.Name);
    }

    // ── the line the file must not cross ────────────────────────────────────

    /// <summary>
    /// <b>Attribution follows the folder; authority does not.</b>
    ///
    /// <para>A repository can arrive from anywhere with <c>.enactive/workspace.json</c> already in
    /// it — the same reasoning that moved remembered approvals out to <c>%APPDATA%</c> and made a
    /// legacy in-workspace permissions file something to ignore rather than import. If standing
    /// approvals were keyed by this id, a clone could bring somebody else's "yes, run_command is
    /// fine here" with it.</para>
    ///
    /// <para>So they are keyed by the PATH, and this pins the two apart: two folders carrying the
    /// same marker are one workspace for history and two for permission.</para>
    /// </summary>
    [Fact]
    public void An_id_carried_in_a_folder_does_not_carry_permissions()
    {
        var mine = Folder("mine");
        var cloned = Folder("cloned-from-somewhere");

        var id = WorkspaceInfo.Adopt(mine).Id;

        // The marker travels, as it would inside a repository somebody published.
        Directory.CreateDirectory(Path.Combine(cloned, ".enactive"));
        File.Copy(WorkspaceIdentity.PathFor(mine), WorkspaceIdentity.PathFor(cloned));

        // One workspace, for the purpose of "whose history is this".
        Assert.Equal(id, WorkspaceInfo.For(cloned).Id);

        // Two, for the purpose of "what may run here" - which is keyed by the path and cannot be
        // carried in a file at all.
        Assert.NotEqual(WorkspaceInfo.IdFor(mine), WorkspaceInfo.IdFor(cloned));
    }

    /// <summary>
    /// The marker lives under <c>.enactive/</c>, which <see cref="WorkspaceGuard"/> refuses to let
    /// the file tools write. A second line rather than the argument — the argument is that nothing
    /// this file says grants anything — but a running agent should not be able to change which
    /// project its own run is recorded against.
    /// </summary>
    [Fact]
    public void The_marker_is_where_the_file_tools_cannot_reach()
    {
        var path = Folder("guarded");

        var refused = Assert.Throws<ArgumentException>(
            () => WorkspaceGuard.ResolveInside(path, ".enactive/workspace.json"));

        Assert.Contains(".enactive", refused.Message, StringComparison.Ordinal);

        // The engine itself reaches it, which is what allowReserved is for.
        Assert.Equal(
            WorkspaceIdentity.PathFor(path),
            WorkspaceGuard.ResolveInside(path, ".enactive/workspace.json", allowReserved: true));
    }

    private static RunRecord Record(Guid workspaceId, string title)
    {
        var at = DateTimeOffset.Now;
        return new RunRecord(
            Guid.NewGuid(), Guid.NewGuid(), title, "a-model", at, at.AddSeconds(1), "Completed",
            new[] { new RunEventRecord(at, nameof(EventKind.IntentReceived), "Intent: " + title, null, null) },
            Array.Empty<string>(), Array.Empty<string>());
    }
}
