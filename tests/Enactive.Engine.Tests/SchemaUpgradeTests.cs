namespace Enactive.Engine.Tests;

using Microsoft.Data.Sqlite;
using Enactive.Core.Context;
using Enactive.Remote.Host;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Opening a database that already has every column does not try to add them again.
///
/// <para>The stores upgraded old tables by running each ALTER every time and catching the error as "already
/// there". On any database but the oldest that throw was the normal path: every start of the app put
/// "SQLite Error 1: 'duplicate column name: schedule_id'" in the debugger's output (2026-10-05), and the catch took
/// every other SqliteException for success as well. The exception was caught, so only watching for it as it is
/// thrown shows it - which is what these tests do, for the code they call and nothing running beside them.</para>
/// </summary>
public sealed class SchemaUpgradeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "enactive-schema", Guid.NewGuid().ToString("N"));

    public SchemaUpgradeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>Marks the flow under test, so other tests' exceptions on other threads are not counted.</summary>
    private static readonly AsyncLocal<bool> Watching = new();

    /// <summary>Every SqliteException thrown inside <paramref name="act"/>, caught or not.</summary>
    private static async Task<List<string>> ThrownWhile(Func<Task> act)
    {
        var thrown = new List<string>();
        void Seen(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Watching.Value && e.Exception is SqliteException sqlite)
                lock (thrown) thrown.Add(sqlite.Message);
        }

        AppDomain.CurrentDomain.FirstChanceException += Seen;
        try
        {
            Watching.Value = true;
            await act();
        }
        finally
        {
            Watching.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= Seen;
        }
        return thrown;
    }

    [Fact]
    public async Task The_workspace_stores_open_an_existing_database_without_an_error()
    {
        var workspace = WorkspaceInfo.For(_root);

        // The first opening creates the tables with every column; the second finds them there, which is
        // what every start of the app after the first does.
        var thrown = await ThrownWhile(async () =>
        {
            for (var opening = 0; opening < 2; opening++)
            {
                await new SqliteRunStore(workspace).LoadSummariesAsync(CancellationToken.None);
                await new SqliteInboxStore(workspace).LoadAllAsync(CancellationToken.None);
            }
        });

        Assert.Empty(thrown);
    }

    [Fact]
    public async Task The_host_store_opens_an_existing_database_without_an_error()
    {
        var path = Path.Combine(_root, "host.db");

        var thrown = await ThrownWhile(() =>
        {
            for (var opening = 0; opening < 2; opening++)
                using (new HostStore(path)) { }
            return Task.CompletedTask;
        });

        Assert.Empty(thrown);
    }
}
