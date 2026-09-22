namespace Enactive.Settings;

using Enactive.Core.Permissions;

/// <summary>
/// Which of the saved roles may call <c>send_email</c>, and how that is changed.
///
/// <para><b>Why this is not a migration.</b> Every other tool that reached only new installations
/// was handed out by <c>WorkerTools.WithImplied</c>, and the argument there is that each one is a
/// capability the worker ALREADY has under another name — editing part of a file when it may
/// rewrite the whole of it. Mail is not that. It is the first tool that acts outside this machine,
/// nothing a worker already holds implies it, and a message cannot be recalled. So it is granted
/// the way <c>delete_file</c> is: by a person, on purpose.</para>
///
/// <para><b>Why it is HERE and not only in the team editor.</b> Because a capability nobody can
/// find does not exist — the lesson this codebase has learnt five times over (<c>edit_file</c>,
/// <c>search_files</c>, <c>create_directory</c>, <c>move_file</c>, <c>copy_file</c>: registered,
/// documented, tested, and named by no role for months). Somebody filling in an SMTP account is
/// telling the application it may send mail; asking them to then find a different screen, pick a
/// role and tick a tool in a list of twelve is how the account ends up configured and unused. The
/// tick lives next to the account, and the pane says plainly when nothing may use it.</para>
///
/// <para>Only <see cref="PermissionLevel.Execute"/> and above are offered. <c>send_email</c>
/// requires Execute at call time, so a tick on the reviewer would be a question that can only ever
/// be answered no — the thing a settings screen must never draw.</para>
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

    /// <summary>
    /// Grants the tool to exactly the named roles and takes it from the other candidates.
    ///
    /// <para>Touches nothing else: a role that cannot carry it, a role named nowhere in
    /// <paramref name="chosen"/> that never had it, and a wildcarded role all come out as they went
    /// in. Idempotent, so the settings window may call it on every save.</para>
    /// </summary>
    public static void Apply(IEnumerable<WorkerConfig> workers, IReadOnlyCollection<string> chosen)
    {
        foreach (var worker in workers)
        {
            if (!CanCarry(worker) || Wildcarded(worker))
                continue;

            var wanted = chosen.Contains(worker.Id, StringComparer.OrdinalIgnoreCase);
            var named = worker.Tools.FindIndex(t => string.Equals(t, Tool, StringComparison.OrdinalIgnoreCase));

            if (wanted && named < 0)
                worker.Tools.Add(Tool);
            else if (!wanted && named >= 0)
                worker.Tools.RemoveAll(t => string.Equals(t, Tool, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Whether ANY role can reach the tool — what the pane says when the answer is no.</summary>
    public static bool AnyoneCanSend(IEnumerable<WorkerConfig> workers)
        => workers.Any(w => CanCarry(w) && Carries(w));
}
