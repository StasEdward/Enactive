namespace Enactive.Tests;

using Microsoft.Data.Sqlite;

/// <summary>
/// Lets go of the SQLite files under a test's own folders, so the folders can be deleted or moved. A pooled connection
/// keeps its file open after it is closed, and Windows will not delete or move a folder with an open file in it.
///
/// <para>Only these files' pools: the tests cleared every pool in the process (<c>SqliteConnection.ClearAllPools</c>),
/// which also closed the connections of test classes running in parallel in the middle of a query - they failed with
/// "Cannot access a disposed object: SQLitePCL.sqlite3", about one full run in six.</para>
/// </summary>
internal static class SqlitePools
{
    /// <summary>Clears the pool of every <c>*.db</c> under each folder; a folder that is not there is skipped.</summary>
    public static void ClearUnder(params string[] folders)
    {
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.db", SearchOption.AllDirectories))
                foreach (var text in ConnectionStrings(file))
                {
                    using var connection = new SqliteConnection(text);
                    SqliteConnection.ClearPool(connection);
                }
        }
    }

    /// <summary>
    /// The connection strings a store could have opened this file with. A pool is found by the text of its connection
    /// string, so each is written as the stores write theirs: the accounts (UserStore) with SqliteConnectionStringBuilder,
    /// a workspace's stores as <c>Data Source=</c> and the path. The two are the same text for a plain path and differ
    /// when the builder quotes one, so both are cleared.
    /// </summary>
    internal static IEnumerable<string> ConnectionStrings(string file)
        => new[] { new SqliteConnectionStringBuilder { DataSource = file }.ToString(), $"Data Source={file}" }.Distinct();
}
