namespace Enactive.Tools;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
/// A Windows Job Object holding one command and everything it starts.
///
/// <para><b>The defect.</b> <c>process.Kill(entireProcessTree: true)</c> reads like a guarantee and
/// is not one: it kills the tree .NET can SEE at that moment, by walking live parents. A command
/// that starts a detached grandchild - <c>start /b</c>, <c>Start-Process</c>, an npm script that
/// backgrounds a watcher - leaves a process whose parent has already exited. Nothing walks to it,
/// so cancelling the run did not cancel it, and it went on writing to the workspace after the run
/// that made it was in the history as finished.</para>
///
/// <para><b>What a job changes.</b> Membership is inherited: every process a member starts is a
/// member too, and there is no way out of it from inside. So the set is exact rather than
/// discovered, and terminating the job terminates all of it at once.</para>
///
/// <para><b>Terminated on cancellation ONLY, and deliberately not on close.</b> An earlier version
/// set <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, so nothing survived the call at all. The argument
/// for it was that <c>run_command</c> answers a question and returns, and a process it left behind
/// is one the agent can neither see nor stop.
///
/// The argument was wrong, and a real task said so within the day: "find Total Commander and start
/// it". Launching a program IS a thing people ask a shell to do, and the program has to be there
/// afterwards. Killing it a second later is not containment, it is breaking the task.
///
/// So the job is a HANDLE ON THIS CALL'S TREE, used when the call is cancelled or times out - which
/// is the defect that was actually reported. A call that finished normally lets go of its tree.
/// Each call has its own job, so cancelling step five does not touch what step one started.</para>
///
/// <para><b>What is lost with it.</b> If this process dies outright, a tree it started is left
/// behind - which is what KILL_ON_JOB_CLOSE was for. That is the smaller harm by a distance: a
/// leftover process after a crash is untidy, and killing the application somebody just asked for is
/// the task failing.</para>
///
/// <para><b>What it may claim.</b> Cancellation actually cancels. It is NOT a security boundary:
/// this is for a mistaken agent, and code that means harm is a different
/// adversary and a different step.</para>
///
/// <para><b>Windows only, and says so.</b> <see cref="Create"/> answers null everywhere else, and
/// the callers then behave exactly as they did before. The Linux equivalent is a process group
/// (<c>setsid</c> plus a signal to the negative pgid), which is a different mechanism with
/// different edges; writing it blind, on a platform this application is not tested on, would be a
/// guard nobody has watched work.</para>
/// </summary>
/// <remarks>
/// Not marked <c>[SupportedOSPlatform("windows")]</c>: every entry point below checks at runtime and
/// does nothing off Windows, and the attribute would push that check onto three call sites that
/// already handle a null job correctly.
/// </remarks>
internal sealed class ProcessJob : IDisposable
{
    private IntPtr _handle;

    private ProcessJob(IntPtr handle) => _handle = handle;

    /// <summary>
    /// A new, empty job - or null when this platform has no job objects, and when the operating
    /// system refuses to make one.
    ///
    /// <para>Null rather than an exception: a shell that cannot be contained is worse than one that
    /// can, and far better than a run that will not start. The caller carries on without it.</para>
    /// </summary>
    public static ProcessJob? Create()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            return null;

        // No limit flags at all, and that is the design - see the class summary. The job is a
        // handle on this call's tree, terminated only when the call is cancelled; it is not a
        // leash that kills whatever the call was asked to start.
        //
        // Deliberately NOT setting ActiveProcessLimit or JobMemoryLimit either, though the same
        // struct carries both and the sandbox plan called them free. They are not free: a ceiling
        // that a real `dotnet build` crosses kills it, and what the model then reads is a command
        // that died with no message - indistinguishable from a broken toolchain, and blamed on one.
        // The number would have to come from measuring real builds.
        return new ProcessJob(handle);
    }

    /// <summary>
    /// Puts a started process in the job, with everything it goes on to start.
    ///
    /// <para>False when the assignment was refused - most plausibly because the process had already
    /// exited, which is not a failure worth reporting: a process that is gone needs no containing.
    /// The caller keeps its old behaviour either way.</para>
    /// </summary>
    public bool Assign(Process process)
    {
        if (_handle == IntPtr.Zero)
            return false;

        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (InvalidOperationException)
        {
            // The process exited and .NET has released its handle.
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Kills every member now, rather than waiting for the handle to close.
    ///
    /// <para>Used on cancellation, where the difference is visible: <c>Dispose</c> arrives at the
    /// end of a using block, and between "the person pressed Stop" and there is a command still
    /// writing files.</para>
    /// </summary>
    public void Terminate()
    {
        if (_handle != IntPtr.Zero)
            TerminateJobObject(_handle, 1);
    }

    /// <summary>
    /// Lets go of the tree. It does NOT kill it - see the class summary: a call that finished
    /// normally leaves what it was asked to start running.
    /// </summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
            CloseHandle(handle);
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    // No SetInformationJobObject, and no limit structs to pass to it. A job with no limits set is
    // the whole design here - see the class summary. They were written, shipped for a day, and
    // taken out again; leaving the declarations behind would say this class configures something.

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
