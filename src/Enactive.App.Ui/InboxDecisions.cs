namespace Enactive.App.Ui;

using Enactive.Agents;
using Enactive.Core.Inbox;

/// <summary>
/// The inbox's side of a question a background run stopped at: which one is waiting for an item's
/// task, and what answering it does. Kept apart from the view model, which is all brushes and
/// bindings, so the part that decides something can be tested without a window.
/// </summary>
internal sealed class InboxDecisions(string workspaceRoot, Func<ParkedDecision, InboxItem, Task>? carryOn)
{
    private readonly DecisionLedger _ledger = new(workspaceRoot);

    /// <summary>
    /// The question this task's run stopped at, if it is still waiting. By the TASK: the answer is
    /// to the task, and a run carried on is a new run under the same one.
    /// </summary>
    public ParkedDecision? WaitingFor(Guid taskId) => _ledger.For(taskId).LastOrDefault(d => !d.Answered);

    /// <summary>
    /// Records the answer against exactly this question, and carries the run on. The ledger refuses
    /// an answer to a question that is no longer waiting - answered from somewhere else, or gone
    /// with its run - and then nothing is carried on. Returns what to tell the person.
    /// </summary>
    public async Task<string> AnswerAsync(ParkedDecision waiting, InboxItem item, string optionId)
    {
        var label = waiting.Options.FirstOrDefault(o => o.Id == optionId)?.Label ?? optionId;
        if (!_ledger.Answer(waiting.TaskId, waiting.RequestId, optionId))
            return "This question is no longer waiting - it was answered already, or its run has ended.";
        if (carryOn is null)
            return $"Answered '{label}'. Run the task again to carry it on.";
        try
        {
            await carryOn(waiting, item);
            return $"Answered '{label}' - the run carries on in the background.";
        }
        catch (Exception ex)
        {
            return $"Answered '{label}', but the run could not be carried on: {ex.Message}";
        }
    }
}
