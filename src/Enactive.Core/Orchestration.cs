namespace Enactive.Core.Orchestration;

using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Intents;

/// <summary>
/// The single seam the UI talks to. Everything starts as an Intent; the whole lifecycle
/// streams back as <see cref="WorkEvent"/>s (PLAN_v2 §4).
/// </summary>
public interface IOrchestrator
{
    IAsyncEnumerable<WorkEvent> SubmitIntentAsync(Intent intent, CancellationToken ct);

    /// <summary>
    /// Carries on from where an interrupted run stopped: the plan it had, the steps it finished, and
    /// what it concluded on the way.
    ///
    /// <para>This is a NEW RUN under the SAME TASK, not the old run continued. That is what the
    /// engine already means by a second attempt at one task, and it is the truthful shape: the first
    /// run's record, if a record was written at all, says what that run did and stops where it
    /// stopped. Pretending one record spans a process that died would mean overwriting the first
    /// half's events with the second half's.</para>
    ///
    /// <para>Steps the checkpoint records as finished are not run again; their cards are emitted so
    /// the plan reads whole. A step that was RUNNING when the process died is run from its
    /// beginning - see <see cref="RunCheckpoint"/> for why nothing finer than a step boundary is
    /// possible here, and what redoing a step costs.</para>
    /// </summary>
    /// <param name="context">
    /// Assembled FRESH by the caller, not restored from the checkpoint. What is on the machine and
    /// what the project has decided are facts about now; a resumed run that carried a three-day-old
    /// environment probe would be reasoning about a computer that has since changed.
    /// </param>
    IAsyncEnumerable<WorkEvent> ResumeRunAsync(
        RunCheckpoint checkpoint, WorkContext context, CancellationToken ct);
}
