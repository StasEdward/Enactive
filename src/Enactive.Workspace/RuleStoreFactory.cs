namespace Enactive.Workspace;

using Enactive.Core.Context;
using Enactive.Core.Rules;

/// <summary>
/// Picks a workspace's rule store off the same ENACTIVE_STORE env var as its runs, memory and inbox: "sqlite" (default),
/// "mysql" or "json" - so everything a workspace persists moves together. The Global layer is not here: it is the
/// machine's, a file beside its settings (<see cref="JsonRuleStore.Global"/>), as its global checks are.
/// </summary>
public static class RuleStoreFactory
{
    public static IRuleStore Create(WorkspaceInfo workspace)
    {
        var kind = (Environment.GetEnvironmentVariable("ENACTIVE_STORE") ?? "sqlite").Trim().ToLowerInvariant();
        return kind switch
        {
            "json" => new JsonRuleStore(Path.Combine(workspace.RootPath, WorkspaceGuard.ReservedFolder, "rules.json")),
            "mysql" => new MySqlRuleStore(
                Environment.GetEnvironmentVariable("ENACTIVE_MYSQL")
                ?? throw new InvalidOperationException(
                    "ENACTIVE_STORE=mysql requires the ENACTIVE_MYSQL connection string, e.g. "
                    + "\"Server=localhost;Database=enactive;User ID=root;Password=...\"."),
                workspace),
            _ => new SqliteRuleStore(workspace)
        };
    }
}
