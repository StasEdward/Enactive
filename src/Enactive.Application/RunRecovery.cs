namespace Enactive.Application;

using Enactive.Core.History;

/// <summary>
/// What carrying on an interrupted run will do again - said before anybody presses Resume (ADR 0002 §6, crash
/// recovery). A checkpoint is written at step boundaries; the step that was running when the process died has no end
/// recorded, so it runs again from its start, and whatever it did outside this machine before the crash - a mail
/// sent, a push, a ticket opened - can happen twice. A file it changed is the run's to see again; a mail is not.
/// </summary>
internal static class RunRecovery
{
    public static string Warning(RunCheckpoint checkpoint)
    {
        // Paused (Plan Phase 4.2): it ended between steps, with nothing running, so nothing runs again - telling the
        // person a step may be done twice would warn them off a resume that redoes nothing. Only when every step left is
        // plain Pending: a paused run can carry BLOCKED steps, and a resume does those again, so the usual warning holds.
        if (checkpoint.Paused && checkpoint.Steps.All(s => s.Status is "Done" or "Failed" or "Skipped" or "Pending"))
            return "It was paused between steps: nothing it did is done again. "
                   + $"{checkpoint.Remaining} step(s) are left; the run continues under the level and worker it was started with.";

        var unfinished = checkpoint.Steps.Where(s => s.Status is not ("Done" or "Failed" or "Skipped")).ToList();
        var interrupted = unfinished.FirstOrDefault(s => s.Status == "Running") ?? unfinished.FirstOrDefault();
        var what = interrupted is null
            ? "The step that was running"
            : $"Step {checkpoint.Steps.ToList().IndexOf(interrupted) + 1}, “{RunTitle.OneLine(interrupted.Title)}”,";
        return $"{what} runs again from its start. Anything it did outside this machine before the run stopped - a mail "
               + "sent, a push, a ticket opened - may be done twice. "
               + $"{checkpoint.Remaining} step(s) are left; the run continues under the level and worker it was started with.";
    }
}
