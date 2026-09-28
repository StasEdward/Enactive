namespace Enactive.Agents;

using Enactive.Core.Execution;
using Enactive.Core.Tools;

/// <summary>What the engine can say about WHO changed a file that differs from how it was when a step began.</summary>
internal enum ChangeAuthor
{
    /// <summary>A call of this step names the path among what it changed.</summary>
    Step,

    /// <summary>
    /// No call of this step names it, but the step ran a call whose writes are not recorded by path -
    /// a command, a script. It can have made the change; so can something outside the run.
    /// </summary>
    StepOrOutside,

    /// <summary>No call of this step names it, and nothing it ran could have changed it.</summary>
    Outside
}

/// <summary>
/// Who changed a file, from the step's own journal - not from the fact that it changed.
///
/// <para><b>Measured 2026-09-28, run 3fe4f8.</b> While a step was reading and searching, the
/// workspace changed around it: three files were deleted and the solution file rewritten, at
/// 10:27:49-52, when the step had made no call that writes and had run no command. The engine
/// compared the workspace with how it was when the step began and told the reviewer
/// "DELETED by this step", and told the worker a file it changed "is still changed until you put
/// it back". The review failed the step, in part, for not reporting deletions it never made. The
/// comparison was right that the files differed. It was wrong about who did it, and it had the
/// journal that says so.</para>
///
/// <para>The engine does not know what a command wrote - its writes are not recorded by file - so a
/// step that ran one gets the honest middle answer, not the convenient one either way.</para>
/// </summary>
internal static class ChangeAuthorship
{
    internal static ChangeAuthor Of(string path, string? oldPath, IReadOnlyList<ExecutedAction> stepActions)
    {
        if (stepActions.Any(a => a.Outcome == ActionOutcome.Succeeded && (Names(a, path) || (oldPath is not null && Names(a, oldPath)))))
            return ChangeAuthor.Step;

        // A call that may have changed the workspace without saying which files: its writes are not
        // recorded by path, so any change could be its. A refused call ran nothing.
        return stepActions.Any(a => a.Outcome != ActionOutcome.Refused
                                    && a.WorkspaceEffect != WorkspaceEffect.None
                                    && a.ChangedPaths is not { Count: > 0 })
            ? ChangeAuthor.StepOrOutside
            : ChangeAuthor.Outside;
    }

    /// <summary>The words that go where "by this step" used to go, unconditionally.</summary>
    internal static string By(ChangeAuthor author) => author switch
    {
        ChangeAuthor.Step => " by this step",
        ChangeAuthor.StepOrOutside => " while this step ran, by no file tool of it (a command it ran can have done it - "
            + "a command's writes are not recorded by file - or something outside the run did; hold the step to it "
            + "only where the evidence shows which)",
        _ => " while this step ran, but NOT by this step (no call of it touched this file and it ran nothing that "
            + "could have: something outside the run did it, and it is not the step's to report or answer for)"
    };

    private static bool Names(ExecutedAction action, string path)
        => action.ChangedPaths?.Any(p => string.Equals(Key(p), Key(path), StringComparison.OrdinalIgnoreCase)) == true;

    private static string Key(string path) => path.Replace('\\', '/').TrimStart('.', '/');
}
