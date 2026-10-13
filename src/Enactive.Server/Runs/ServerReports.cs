namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Server.Accounts;

/// <summary>
/// The two reports the window shows a workspace as in its viewer - the Timeline (its project memory) and the Environment
/// (the machine it runs on) - for anybody signed in, as the rail offers them; in the window's words (WorkspaceReports).
///
/// <para><b>Only the workspaces the service knows, under its roots</b> (KnownWorkspaces), as the history is read
/// (ServerHistory): a Viewer naming any folder would read whatever memory sat under it, and probe git in it. The
/// environment is the service's own machine, probed - a read, but one that runs short commands (git, docker, wsl), so a
/// page asks for it once per workspace, not on every redraw, and the service probes a workspace at most once a
/// <see cref="ProbeKept"/> (see there).</para>
/// </summary>
/// <param name="clock">The time a probe is kept by; null, the machine's.</param>
internal sealed class ServerReports(UserStore users, KnownWorkspaces workspaces, ProbeEnvironment probe, TimeProvider? clock = null)
{
    /// <summary>
    /// How long one probe of a workspace answers for it. Each probe starts programs as the service's account, and any
    /// Viewer can ask for one by opening the page again and again - with several people, or one reloading, that was a
    /// stream of processes on the server for a report that changes in minutes, not seconds.
    /// </summary>
    public static readonly TimeSpan ProbeKept = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, Task<EnvironmentReport> Report)> _probes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The workspace's project memory, or why it is not shown.</summary>
    public async Task<(string? Text, string? Problem)> TimelineAsync(ClaimsPrincipal user, string root, CancellationToken ct)
        => Known(user, root) is { } known
            ? (await WorkspaceReports.TimelineAsync(known, ct), null)
            : (null, Why(user));

    /// <summary>The service's machine as the workspace sees it - the probe's summary and a snapshot - or why it is not shown.</summary>
    public async Task<(string? Text, string? Problem)> EnvironmentAsync(ClaimsPrincipal user, string root, CancellationToken ct)
        => Known(user, root) is { } known
            ? ((await ProbeAsync(known).WaitAsync(ct)).Text, null)
            : (null, Why(user));

    /// <summary>
    /// The workspace's probe of the last <see cref="ProbeKept"/>, or a new one - one at a time per workspace: pages asking
    /// together wait for the same. A probe that failed is not kept, so the next look tries again. Not cancelled with the
    /// page that started it: another may be waiting for it.
    /// </summary>
    private Task<EnvironmentReport> ProbeAsync(string root)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_probes.TryGetValue(root, out var kept) && now - kept.At < ProbeKept && !Failed(kept.Report))
                return kept.Report;
            var report = WorkspaceReports.EnvironmentAsync(root, probe, CancellationToken.None);
            _probes[root] = (now, report);
            return report;
        }
    }

    private static bool Failed(Task<EnvironmentReport> report)
        => report.IsCompleted && (!report.IsCompletedSuccessfully || report.Result.OneLine is null);

    private string? Known(ClaimsPrincipal user, string root)
        => ServerAuth.StillValid(users, user) is { Role: >= Role.Viewer } ? workspaces.Find(root)?.RootPath : null;

    private string Why(ClaimsPrincipal user)
        => ServerAuth.StillValid(users, user) is null
            ? "Your session has ended. Sign in again."
            : "The service does not know that folder as a workspace.";
}
