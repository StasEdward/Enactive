namespace Enactive.Workspace;

using Enactive.Core.Context;
using Enactive.Core.Memory;

/// <summary>
/// Picks the project-memory store the same way <see cref="RunStoreFactory"/> picks the run store, off
/// the same ENACTIVE_STORE env var: "sqlite" (default), "mysql", or "json" - so a workspace's history
/// and its memory always land in the same place, and one setting moves both.
/// MySQL also needs ENACTIVE_MYSQL (an ADO.NET connection string).
/// </summary>
public static class MemoryStoreFactory
{
    public static IMemoryStore Create(WorkspaceInfo workspace)
    {
        var kind = (Environment.GetEnvironmentVariable("ENACTIVE_STORE") ?? "sqlite")
            .Trim().ToLowerInvariant();

        return kind switch
        {
            "json" => new JsonMemoryStore(workspace),
            "mysql" => new MySqlMemoryStore(
                Environment.GetEnvironmentVariable("ENACTIVE_MYSQL")
                ?? throw new InvalidOperationException(
                    "ENACTIVE_STORE=mysql requires the ENACTIVE_MYSQL connection string, e.g. "
                    + "\"Server=localhost;Database=enactive;User ID=root;Password=...\"."),
                workspace),
            _ => new SqliteMemoryStore(workspace)
        };
    }
}
