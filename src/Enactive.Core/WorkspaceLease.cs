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
}

/// <summary>
/// A run refused because another process is running in its workspace. Its own type, so the schedule runner can put
/// the occurrence off to the next tick instead of reporting the schedule as failed.
/// </summary>
public sealed class WorkspaceInUseException(string workspaceRoot, LeaseHolder? holder)
    : InvalidOperationException(Describe(workspaceRoot, holder))
{
    public string WorkspaceRoot { get; } = workspaceRoot;

    /// <summary>Who holds it; null when it had not yet written its name.</summary>
    public LeaseHolder? Holder { get; } = holder;

    public static string Describe(string workspaceRoot, LeaseHolder? holder)
        => $"Another Enactive process is running in '{workspaceRoot}'"
           + (holder is null ? "" : $": {holder.Describe()}")
           + ". Two agents in one folder would edit the same files; wait for it to finish, or stop it there.";
}
