namespace Enactive.Server.Tests.Components;

using System.Security.Claims;
using Bunit;
using Enactive.Application;
using Enactive.Core.Permissions;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// What a page test stands on: the service's own services, registered as the server registers them (ServerApp.AddServices,
/// over a data folder of its own), three accounts - <c>ada</c> an Admin, <c>alice</c> an Operator, <c>vera</c> a Viewer -
/// and <see cref="SignIn"/> to be one of them. Without it every page test would build its own half of the service, and
/// what a Viewer or an Operator sees would be tested against a host that does not check roles the way the server does.
/// </summary>
public abstract class ComponentHost : BunitContext
{
    private const string Password = "correct horse battery";

    // Every wait for a page to draw (WaitForAssertion, WaitForState) gets 10 s, once for all the page tests. bUnit's own
    // second was enough alone and not under a full parallel run, where pages that read on a thread of their own failed
    // for having taken a little longer.
    static ComponentHost() => DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-components", Guid.NewGuid().ToString("N"));
    private readonly SignedIn _signedIn = new();

    protected ComponentHost()
    {
        var data = Path.Combine(_root, "data");
        // The same server.db the services open: the store keeps nothing in memory, so a second one on the file sees the
        // same accounts the services check.
        Users = new UserStore(data);
        Users.Add("ada", Password, Role.Admin);
        Users.Add("alice", Password, Role.Operator);
        Users.Add("vera", Password, Role.Viewer);

        // The service's workspaces are only those under its roots (KnownWorkspaces). Every test's folders are made under
        // the temp folder, so that is the root unless a test names its own (registered after, it wins); with none, no
        // page would have a workspace to show.
        ServerApp.AddServices(Services, new ServerOptions { WorkspaceRoots = [Path.GetTempPath()] }, data);
        // Registered after the server's, so these win: a run that ends at once instead of the engine (which would
        // compose a model from settings nobody wrote here), and a fixed signed-in person instead of the circuit's.
        Services.AddSingleton<IRunExecutor>(new EndsAtOnce(new ApprovalStore(Path.Combine(data, "permissions.json"))));
        Services.AddScoped<AuthenticationStateProvider>(_ => _signedIn);
        // bUnit registers a stand-in that throws for every authorization check, and the server's registration of the
        // real one (with the Viewer, Operator and Admin policies) does not replace it: a page asking whether the
        // person may do something failed before it was shown.
        Services.AddSingleton<IAuthorizationService, DefaultAuthorizationService>();
        // A dialog's hold on the page is a script (Ui/Dialog.razor.js), which bUnit has no browser for: its calls are
        // recorded, for a test to check they were made, and answer nothing.
        DialogScript = JSInterop.SetupModule("./Components/Ui/Dialog.razor.js");
        DialogScript.Mode = JSRuntimeMode.Loose;
        // The command bar's keys (Runs/CommandBar.razor.js) likewise: Enter, Ctrl+Enter and Ctrl+K are the browser's.
        CommandBarScript = JSInterop.SetupModule("./Components/Runs/CommandBar.razor.js");
        CommandBarScript.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>The dialog's script, as bUnit stands in for it: what was called on it.</summary>
    internal BunitJSModuleInterop DialogScript { get; }

    /// <summary>The command bar's script, as bUnit stands in for it.</summary>
    internal BunitJSModuleInterop CommandBarScript { get; }

    /// <summary>The service's accounts, the real store.</summary>
    internal UserStore Users { get; }

    /// <summary>
    /// From now on the pages render for this account: its principal, as sign-in builds it, is the authentication state
    /// they are given. Before any call nobody is signed in.
    /// </summary>
    public void SignIn(string name)
        => _signedIn.Set(ServerAuth.PrincipalOf(Users.Find(name) ?? throw new ArgumentException($"No account '{name}'.", nameof(name))));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;
        Enactive.Tests.SqlitePools.ClearUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>The person the page is shown to; a state of its own, as a circuit's is.</summary>
    private sealed class SignedIn : AuthenticationStateProvider
    {
        private ClaimsPrincipal _user = new(new ClaimsIdentity());

        public void Set(ClaimsPrincipal user)
        {
            _user = user;
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(_user));
    }

    /// <summary>The engine, stood in for: a run begins and finishes with no step, so a page can start one and see it end.</summary>
    private sealed class EndsAtOnce(ApprovalStore approvals) : IRunExecutor
    {
        public string? Problem => null;
        public SessionApprovals Session { get; } = new();
        public ApprovalStore Approvals { get; } = approvals;

        public Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct)
            => run.Session.DrainAsync(System.Linq.AsyncEnumerable.Empty<Enactive.Core.Events.WorkEvent>(), ct);
    }
}
