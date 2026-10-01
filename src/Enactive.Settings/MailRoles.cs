namespace Enactive.Settings;

using Enactive.Core.Permissions;

/// <summary>
/// Which of the saved roles may call <c>send_email</c> - read by the SMTP pane, never written by it. The tool is
/// granted under the role, with every other tool, in the team editor.
///
/// <para><b>Why this is not a migration.</b> Every other tool that reached only new installations
/// was handed out by <c>WorkerTools.WithImplied</c>, and the argument there is that each one is a
/// capability the worker ALREADY has under another name — editing part of a file when it may
/// rewrite the whole of it. Mail is not that. It is the first tool that acts outside this machine,
/// nothing a worker already holds implies it, and a message cannot be recalled. So it is granted
/// the way <c>delete_file</c> is: by a person, on purpose.</para>
///
/// <para><b>Why the SMTP pane reports it.</b> A capability nobody can find does not exist - the lesson
/// this codebase has learnt five times over (<c>edit_file</c>, <c>search_files</c>, <c>create_directory</c>,
/// <c>move_file</c>, <c>copy_file</c>: registered, documented, tested, and named by no role for months).
/// So the pane next to the account says who may send, and says plainly when nobody may.</para>
///
/// <para>Only a role at <see cref="PermissionLevel.Execute"/> or above counts: <c>send_email</c>
/// requires Execute at call time, so a read-only role that names the tool still cannot send.</para>
/// </summary>
public static class MailRoles
{
    public const string Tool = "send_email";

    /// <summary>The wildcard that means "every tool", which therefore includes this one.</summary>
    private const string Everything = "*";

    /// <summary>A role whose level allows the tool at all — see the note on the class.</summary>
    public static bool CanCarry(WorkerConfig worker) => worker.Level >= PermissionLevel.Execute;

    /// <summary>Whether this role's saved list already reaches the tool.</summary>
    public static bool Carries(WorkerConfig worker)
        => Wildcarded(worker) || worker.Tools.Contains(Tool, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A role granted <c>*</c> carries the tool and cannot be refused it here: taking it away would
    /// mean rewriting a wildcard somebody chose into a list they did not. The pane shows it as on
    /// and leaves it alone.
    /// </summary>
    public static bool Wildcarded(WorkerConfig worker)
        => worker.Tools.Contains(Everything, StringComparer.Ordinal);

    /// <summary>Whether ANY role can reach the tool — what the pane says when the answer is no.</summary>
    public static bool AnyoneCanSend(IEnumerable<WorkerConfig> workers)
        => workers.Any(w => CanCarry(w) && Carries(w));
}
