namespace Enactive.Workspace;

using Enactive.Core.Context;
using Enactive.Core.History;

/// <summary>
/// Picks the run store by the ENACTIVE_STORE env var: "sqlite" (default), "mysql", or "json".
/// MySQL also needs ENACTIVE_MYSQL (an ADO.NET connection string).
/// </summary>
public static class RunStoreFactory
{
    public static IRunStore Create(WorkspaceInfo workspace)
    {
        var kind = (Environment.GetEnvironmentVariable("ENACTIVE_STORE") ?? "sqlite")
            .Trim().ToLowerInvariant();

        return kind switch
        {
            "json" => new JsonRunStore(workspace),
            "mysql" => new MySqlRunStore(
                Environment.GetEnvironmentVariable("ENACTIVE_MYSQL")
                ?? throw new InvalidOperationException(
                    "ENACTIVE_STORE=mysql requires the ENACTIVE_MYSQL connection string, e.g. "
                    + "\"Server=localhost;Database=enactive;User ID=root;Password=...\".")),
            _ => new SqliteRunStore(workspace)
        };
    }
}
