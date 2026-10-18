namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Core.Checks;
using Enactive.Server.Accounts;
using Enactive.Workspace;

/// <summary>
/// The check manifests (Plan Phase 3.5), kept from the browser: the service's global ones in its settings, a workspace's from
/// the Checks screen. The window's list (ChecksViewModel) over the store every run reads (CheckStore), changed here as the
/// person.
///
/// <para><b>Who.</b> The global checks judge every run in every workspace the service has, as its settings do: an Admin's,
/// like the settings page. A workspace's checks judge that workspace's runs, as its templates and schedules shape them: an
/// Operator's (ADR 0002 §4). Anybody signed in reads them. Checked at each call; every change audited, as a template's is.</para>
///
/// <para><b>Where.</b> The global folder is the one the service's engine reads, <c>&lt;data folder&gt;/checks</c>
/// (ServerFolders.ChecksOf) - not CheckStore's default %APPDATA%, which a service started with its own data folder never
/// reads - and it is known without an engine: a service with no model configured yet can have its checks set up.</para>
///
/// <para><b>When.</b> Not while a run works in the workspace - the service's own, or any other program's that holds the
/// workspace's lease: the run puts back its check manifests at its end as they were when it started (CheckManifestGuard), and
/// a change made meanwhile would be undone without a word.</para>
/// </summary>
internal sealed class ServerChecks(UserStore users, ServerFolders folders, IRunControl runs, KnownWorkspaces workspaces)
{
    /// <summary>The service's global check manifests.</summary>
    public string GlobalFolder => folders.Checks;

    /// <summary>
    /// The global list, for the settings page: over <paramref name="root"/> - the workspace the person is in, when the service
    /// knows it - so it says which global check that workspace shadows and what the implicit build and test are there.
    /// Changed only by an Admin; anybody else reads it.
    /// </summary>
    public (ChecksViewModel? List, string? Problem) Global(ClaimsPrincipal user, string? root)
    {
        if (ServerAuth.StillValid(users, user) is not { } account)
            return (null, Ended);
        var known = root is { Length: > 0 } ? workspaces.Find(root) : null;
        var store = new CheckStore(known?.RootPath, GlobalFolder, KnownEcosystems.All);
        return (new ChecksViewModel(store, CheckScope.Global,
            account.Role >= Role.Admin ? new AsThePerson(this, user, known?.RootPath) : null), null);
    }

    /// <summary>A workspace's list, for its Checks screen: changed by an Operator, read by anybody signed in.</summary>
    public (ChecksViewModel? List, string? Problem) Workspace(ClaimsPrincipal user, string root)
    {
        if (ServerAuth.StillValid(users, user) is not { } account)
            return (null, Ended);
        if (workspaces.Find(root) is not { } known)
            return (null, Unknown);
        var store = new CheckStore(known.RootPath, GlobalFolder, KnownEcosystems.All);
        return (new ChecksViewModel(store, CheckScope.Workspace,
            account.Role >= Role.Operator ? new AsThePerson(this, user, known.RootPath) : null), null);
    }

    /// <summary>Why this person may not change <paramref name="scope"/> now, or null - the role, and a run working there.</summary>
    public string? WhyNot(ClaimsPrincipal user, string? root, CheckScope scope)
    {
        if (ServerAuth.StillValid(users, user) is not { } account)
            return Ended;
        if (scope == CheckScope.Global)
            return account.Role >= Role.Admin ? null : "Your role does not include changing the service's global checks.";
        if (account.Role < Role.Operator)
            return "Your role does not include changing a workspace's checks.";
        if (root is not { Length: > 0 } || workspaces.Find(root) is not { } known)
            return Unknown;
        // Any run that holds the workspace (WorkspaceLease) - this service's, or another program's on this machine - and the
        // service's own runs not yet composed, which hold no lease yet and will at their start.
        return RunWorksIn(user, known.RootPath) ? CheckChangeWords.RunActive : CheckChangeWords.WhyNotNow(known.RootPath);
    }

    public string? Save(ClaimsPrincipal user, string? root, string id, CheckManifestSaid said, CheckScope scope)
    {
        if (WhyNot(user, root, scope) is { } why)
            return why;
        var store = Store(root);
        if (store.Validate(id, said, scope) is { Count: > 0 } problems)
            return CheckChangeWords.NotSaved(problems);
        try
        {
            store.Save(id, said, scope);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "Not saved - " + ex.Message;
        }
        users.Audit(Who(user), "check saved", $"{id} ({scope}) in {Where(store, scope)}");
        return null;
    }

    public string? Delete(ClaimsPrincipal user, string? root, string id, CheckScope scope)
    {
        if (WhyNot(user, root, scope) is { } why)
            return why;
        var store = Store(root);
        try
        {
            store.Delete(id, scope);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "Not deleted - " + ex.Message;
        }
        users.Audit(Who(user), "check deleted", $"{id} ({scope}) in {Where(store, scope)}");
        return null;
    }

    /// <summary>Any run of the service still going in that folder, whoever started it.</summary>
    // One rule for "the same workspace folder" (WorkspaceActivity.SameFolder, the one the activity registry keys by): this
    // and ServerHistory each wrote their own, one ignoring case everywhere and the other only on Windows.
    private bool RunWorksIn(ClaimsPrincipal user, string root)
        => runs.List(user).Any(r => !r.Ended.IsCompleted && Enactive.Core.Storage.WorkspaceActivity.SameFolder(r.Workspace, root));

    private CheckStore Store(string? root)
        => new(root is { Length: > 0 } && workspaces.Find(root) is { } known ? known.RootPath : null, GlobalFolder, KnownEcosystems.All);

    private static string Where(CheckStore store, CheckScope scope)
        => scope == CheckScope.Workspace ? store.WorkspaceFolder ?? "" : store.GlobalFolder;

    private static string Who(ClaimsPrincipal user) => user.Identity?.Name ?? "?";

    private const string Ended = "Your session has ended. Sign in again.";
    private const string Unknown = "The service does not know that folder as a workspace.";

    /// <summary>The list's changes, as the person whose page it is: each through this service's checks and its audit.</summary>
    private sealed class AsThePerson(ServerChecks checks, ClaimsPrincipal user, string? root) : ICheckChanges
    {
        public string? WhyNot(CheckScope scope) => checks.WhyNot(user, root, scope);

        public string? Save(string id, CheckManifestSaid said, CheckScope scope) => checks.Save(user, root, id, said, scope);

        public string? Delete(string id, CheckScope scope) => checks.Delete(user, root, id, scope);
    }
}
