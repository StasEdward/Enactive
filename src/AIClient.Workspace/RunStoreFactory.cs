namespace AIClient.Workspace;

using AIClient.Core.Context;
using AIClient.Core.History;

/// <summary>
/// Picks the run store by the AICLIENT_STORE env var: "sqlite" (default), "mysql", or "json".
/// MySQL also needs AICLIENT_MYSQL (an ADO.NET connection string).
/// </summary>
public static class RunStoreFactory
{
    public static IRunStore Create(WorkspaceInfo workspace)
    {
        var kind = (Environment.GetEnvironmentVariable("AICLIENT_STORE") ?? "sqlite")
            .Trim().ToLowerInvariant();

        return kind switch
        {
            "json" => new JsonRunStore(workspace),
            "mysql" => new MySqlRunStore(
                Environment.GetEnvironmentVariable("AICLIENT_MYSQL")
                ?? throw new InvalidOperationException(
                    "AICLIENT_STORE=mysql requires the AICLIENT_MYSQL connection string, e.g. "
                    + "\"Server=localhost;Database=aiclient;User ID=root;Password=...\".")),
            _ => new SqliteRunStore(workspace)
        };
    }
}
