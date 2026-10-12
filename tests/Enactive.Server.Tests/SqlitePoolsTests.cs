namespace Enactive.Server.Tests;

using Enactive.Core.Context;
using Enactive.Server.Accounts;
using Enactive.Tests;
using Enactive.Workspace;

/// <summary>
/// The tests' own SQLite files let go, and nobody else's (tests/Shared/SqlitePools.cs): a folder whose database a
/// pooled connection still holds cannot be deleted on Windows, and clearing every pool in the process broke test classes
/// running beside it in the middle of a query.
/// </summary>
public sealed class SqlitePoolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-pools", Guid.NewGuid().ToString("N"));

    public SqlitePoolsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqlitePools.ClearUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    /// <summary>The accounts' file, opened as UserStore opens it (the connection-string builder).</summary>
    [Fact]
    public void The_accounts_file_is_let_go_and_its_folder_deletes()
    {
        var folder = Folder("accounts");
        new UserStore(folder).Add("ada", "correct horse battery", Role.Admin);

        SqlitePools.ClearUnder(folder);

        Directory.Delete(folder, recursive: true);
        Assert.False(Directory.Exists(folder));
    }

    /// <summary>A workspace's file, opened as its stores open it ("Data Source=" and the path).</summary>
    [Fact]
    public async Task A_workspace_s_file_is_let_go_and_its_folder_deletes()
    {
        var folder = Folder("workspace");
        await Workspaced(folder);

        SqlitePools.ClearUnder(folder);

        Directory.Delete(folder, recursive: true);
        Assert.False(Directory.Exists(folder));
    }

    /// <summary>Only the folder named: another one's pooled connection is left as it was, still holding its file.</summary>
    [Fact]
    public async Task Another_folder_s_file_is_left_alone()
    {
        var mine = Folder("mine");
        var theirs = Folder("theirs");
        await Workspaced(mine);
        await Workspaced(theirs);

        SqlitePools.ClearUnder(mine);

        Directory.Delete(mine, recursive: true);
        Assert.Throws<IOException>(() => Directory.Delete(theirs, recursive: true));
    }

    /// <summary>A run saved through a workspace's run store, which leaves its connection in the pool.</summary>
    private static async Task Workspaced(string folder)
    {
        var workspace = WorkspaceInfo.For(folder);
        var at = DateTimeOffset.Now;
        await new SqliteRunStore(workspace).SaveAsync(new Enactive.Core.History.RunRecord(Guid.NewGuid(), Guid.NewGuid(),
            "a run", "model", at, at, "Completed", [], [], []), CancellationToken.None);
    }
}
