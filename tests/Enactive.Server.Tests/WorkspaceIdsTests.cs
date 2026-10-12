namespace Enactive.Server.Tests;

using Enactive.Core.Context;
using Enactive.Server.Runs;
using Enactive.Workspace;

/// <summary>
/// A workspace named by its id in an address: a Windows path does not survive a URL segment, so the pages carry the id
/// the folder carries and the service finds the folder again from it - among the workspaces it knows, never from a
/// path a caller supplies.
/// </summary>
public sealed class WorkspaceIdsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-workspace-ids", Guid.NewGuid().ToString("N"));
    private readonly ServerEngine _engine;
    private readonly WorkspaceIds _ids;

    public WorkspaceIdsTests()
    {
        Directory.CreateDirectory(_root);
        _engine = new ServerEngine(_root, new FileLogSink(Path.Combine(_root, "logs")));
        _ids = new WorkspaceIds(_engine);
    }

    public void Dispose()
    {
        _engine.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private string Folder(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void A_known_workspace_is_found_by_its_id()
    {
        var folder = Folder("project");
        _engine.Workspaces.Touch(folder);

        var id = _ids.IdOf(folder);
        var found = _ids.Find(id);

        Assert.Equal(WorkspaceInfo.For(folder).Id, id);
        Assert.NotNull(found);
        Assert.Equal(Path.GetFullPath(folder), Path.GetFullPath(found!.RootPath));
    }

    [Fact]
    public void An_unknown_id_finds_nothing()
    {
        _engine.Workspaces.Touch(Folder("project"));

        Assert.Null(_ids.Find(Guid.NewGuid()));
    }

    [Fact]
    public void A_folder_not_known_is_not_found_by_its_id()
    {
        _engine.Workspaces.Touch(Folder("project"));
        var stranger = Folder("stranger");

        Assert.Null(_ids.Find(_ids.IdOf(stranger)));
    }

    [Fact]
    public void A_known_folder_that_is_gone_is_skipped_not_thrown()
    {
        var gone = Folder("gone");
        var kept = Folder("kept");
        _engine.Workspaces.Touch(gone);
        _engine.Workspaces.Touch(kept);
        Directory.Delete(gone, recursive: true);

        Assert.NotNull(_ids.Find(_ids.IdOf(kept)));
    }
}
