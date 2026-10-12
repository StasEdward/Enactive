namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Server.Accounts;

/// <summary>
/// The two reports the window shows a workspace as in its viewer - the Timeline (its project memory) and the Environment
/// (the machine it runs on) - for anybody signed in, as the rail offers them; in the window's words (WorkspaceReports).
///
/// <para><b>Only the workspaces the service knows</b>, as the history is read (ServerHistory): a Viewer naming any folder
/// would read whatever memory sat under it, and probe git in it. The environment is the service's own machine, probed now
/// - a read, but one that runs short commands (git, docker, wsl), so a page asks for it once per workspace, not on every
/// redraw.</para>
/// </summary>
internal sealed class ServerReports(UserStore users, ServerEngine engine, ProbeEnvironment probe)
{
    /// <summary>The workspace's project memory, or why it is not shown.</summary>
    public async Task<(string? Text, string? Problem)> TimelineAsync(ClaimsPrincipal user, string root, CancellationToken ct)
        => Known(user, root) is { } known
            ? (await WorkspaceReports.TimelineAsync(known, ct), null)
            : (null, Why(user));

    /// <summary>The service's machine as the workspace sees it - the probe's summary and a snapshot - or why it is not shown.</summary>
    public async Task<(string? Text, string? Problem)> EnvironmentAsync(ClaimsPrincipal user, string root, CancellationToken ct)
        => Known(user, root) is { } known
            ? ((await WorkspaceReports.EnvironmentAsync(known, probe, ct)).Text, null)
            : (null, Why(user));

    private string? Known(ClaimsPrincipal user, string root)
        => ServerAuth.StillValid(users, user) is { Role: >= Role.Viewer } ? engine.Workspaces.Find(root)?.RootPath : null;

    private string Why(ClaimsPrincipal user)
        => ServerAuth.StillValid(users, user) is null
            ? "Your session has ended. Sign in again."
            : "The service does not know that folder as a workspace.";
}
