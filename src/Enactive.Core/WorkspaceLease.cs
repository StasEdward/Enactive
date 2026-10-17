namespace Enactive.Core.Storage;

using Enactive.Core.Context;

/// <summary>
/// One process running in a workspace at a time: the window, a console, the service (ADR 0002, server plan A5).
///
/// <para><b>Why.</b> Each host kept its own runs apart - the window its own, the schedule runner by its claim on a
/// schedule - and nothing kept two hosts apart. A console started from a scheduler while the window ran a task in the
/// same folder, or the service and the desktop of another account, were two agents editing the same files, each
/// reviewing work the other had changed underneath it. A lock on the settings folder does not catch it:
/// <c>%APPDATA%</c> is per account, the workspace is not.</para>
///
/// <para>The lease file is <c>.enactive/lease</c>, under the folder no tool may write (<see cref="WorkspaceGuard"/>).
/// Taken by the run composer for every run, so no host can forget it; a host that must decide BEFORE composing - the
/// schedule runner, which marks an occurrence fired - takes it itself first, and the composer's take then counts.</para>
/// </summary>
public static class WorkspaceLease
{
    /// <summary>The lease file of a workspace.</summary>
    public static string PathFor(string workspaceRoot)
        => Path.Combine(workspaceRoot, WorkspaceGuard.ReservedFolder, "lease");

    /// <summary>Takes the workspace for this process, or refuses with who holds it.</summary>
    /// <exception cref="WorkspaceInUseException">Another process is running in this workspace.</exception>
    public static ProcessLease Take(string workspaceRoot)
        => ProcessLease.TryTake(PathFor(workspaceRoot), out var holder)
           ?? throw new WorkspaceInUseException(workspaceRoot, holder);

    /// <summary>Takes the workspace for this process, or returns null and who holds it.</summary>
    public static ProcessLease? TryTake(string workspaceRoot, out LeaseHolder? holder)
        => ProcessLease.TryTake(PathFor(workspaceRoot), out holder);

    /// <summary>
    /// Whether a run is working in the workspace now - this process's or another's (the window, a console, the service) -
    /// and who, when it is another. Asked by whatever must not change the workspace under a run: every run holds this from
    /// the moment it is composed until it is disposed, whoever started it.
    /// </summary>
    public static bool IsHeld(string workspaceRoot, out LeaseHolder? elsewhere)
        => ProcessLease.IsHeld(PathFor(workspaceRoot), out elsewhere);
}

/// <summary>
/// A run refused because another process is running in its workspace - or, in this process, a "Run checks now"
/// (<see cref="WorkspaceActivity"/>). Its own type, so the schedule runner can put the occurrence off to the next tick
/// instead of reporting the schedule as failed.
/// </summary>
/// <param name="reason">What is in the way when it is not another process - said instead of <see cref="Describe"/>.</param>
public sealed class WorkspaceInUseException(string workspaceRoot, LeaseHolder? holder, string? reason = null)
    : InvalidOperationException(reason ?? Describe(workspaceRoot, holder))
{
    public string WorkspaceRoot { get; } = workspaceRoot;

    /// <summary>Who holds it; null when it had not yet written its name.</summary>
    public LeaseHolder? Holder { get; } = holder;

    public static string Describe(string workspaceRoot, LeaseHolder? holder)
        => $"Another Enactive process is running in '{workspaceRoot}'"
           + (holder is null ? "" : $": {holder.Describe()}")
           + ". Two agents in one folder would edit the same files; wait for it to finish, or stop it there.";
}

/// <summary>
/// What this PROCESS is doing in a workspace - its runs, and a "Run checks now" (Plan Phase 3.2) - kept apart within it.
///
/// <para><b>Why beside the lease.</b> The lease (<see cref="WorkspaceLease"/>) keeps other processes out and counts this
/// one's takes, because the window runs a foreground and background runs side by side. So it let two people on the
/// service press "Run checks now" at once - the same commands side by side in one folder - and let a run start while the
/// checks read the files it was changing, and the other way round. Checks run one at a time in a workspace, and never
/// beside a run of this process; runs beside each other stay the run queue's to decide.</para>
/// </summary>
public static class WorkspaceActivity
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (int Runs, bool Checks)> Now =
        new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Why the checks cannot start: they are running already.</summary>
    public const string ChecksBusy = "The checks are already running in this workspace; their record is listed when they are done.";

    /// <summary>Why the checks cannot start: a run is going.</summary>
    public const string RunBusy = "A run is going in this workspace; run the checks when it has ended, so they see the files as you left them.";

    /// <summary>Why a run cannot start: the checks are running.</summary>
    public const string ChecksRunning = "The checks are running in this workspace (Run checks now); start the run when they are done - "
        + "a run started now would change the files they are checking.";

    /// <summary>Holds the workspace for a "Run checks now", or null and why not: checks already running, or a run going.</summary>
    public static IDisposable? TryChecks(string workspaceRoot, out string? why)
    {
        var key = Key(workspaceRoot);
        lock (Gate)
        {
            var (runs, checks) = Now.GetValueOrDefault(key);
            why = checks ? ChecksBusy : runs > 0 ? RunBusy : null;
            if (why is not null)
                return null;
            Now[key] = (runs, true);
        }
        return new Hold(() => Change(key, (r, _) => (r, false)));
    }

    /// <summary>Counts a run in the workspace, or null and why not: the checks are running there.</summary>
    public static IDisposable? TryRun(string workspaceRoot, out string? why)
    {
        var key = Key(workspaceRoot);
        lock (Gate)
        {
            var (runs, checks) = Now.GetValueOrDefault(key);
            why = checks ? ChecksRunning : null;
            if (why is not null)
                return null;
            Now[key] = (runs + 1, false);
        }
        return new Hold(() => Change(key, (r, c) => (Math.Max(0, r - 1), c)));
    }

    private static void Change(string key, Func<int, bool, (int, bool)> change)
    {
        lock (Gate)
        {
            var (runs, checks) = Now.GetValueOrDefault(key);
            var next = change(runs, checks);
            if (next is (0, false)) Now.Remove(key);
            else Now[key] = next;
        }
    }

    private static string Key(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>
    /// Whether two paths are one workspace folder, by the rule this registry keys folders by: full paths, a trailing
    /// separator ignored, case ignored where the file system ignores it (Windows, macOS) and kept where it does not.
    /// </summary>
    public static bool SameFolder(string a, string b) => Now.Comparer.Equals(Key(a), Key(b));

    private sealed class Hold(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                release();
        }
    }
}
