namespace Enactive.Workspace;

using Enactive.Core.Context;
using Enactive.Core.Inbox;

/// <summary>
/// Picks the inbox store off the same ENACTIVE_STORE env var as the run and memory stores: "sqlite"
/// (default), "mysql", or "json" - so everything a workspace persists moves together.
/// MySQL also needs ENACTIVE_MYSQL (an ADO.NET connection string).
/// </summary>
public static class InboxStoreFactory
{
    public static IInboxStore Create(WorkspaceInfo workspace)
    {
        var kind = (Environment.GetEnvironmentVariable("ENACTIVE_STORE") ?? "sqlite")
            .Trim().ToLowerInvariant();

        return kind switch
        {
            "json" => new JsonInboxStore(workspace),
            "mysql" => new MySqlInboxStore(
                Environment.GetEnvironmentVariable("ENACTIVE_MYSQL")
                ?? throw new InvalidOperationException(
                    "ENACTIVE_STORE=mysql requires the ENACTIVE_MYSQL connection string, e.g. "
                    + "\"Server=localhost;Database=enactive;User ID=root;Password=...\"."),
                workspace),
            _ => new SqliteInboxStore(workspace)
        };
    }
}
