namespace Enactive.Agents;

using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;

/// <summary>
/// Who this run is, what it has spent, and what it has produced — and, from those, every event it
/// emits.
///
/// <para>These five factories used to be local functions inside the run body, closing over
/// <c>runId</c>, <c>taskId</c>, <c>budget</c> and <c>artifacts</c>. That is not four incidental
/// captures: it is a SCOPE, and stamping every event with it is the only reason the factories exist.
/// A UI attributes an event to a run and a step by these fields, so an event built without them is
/// an event nothing can place.</para>
///
/// <para><c>FIX_PLAN.md</c> §9d measured the split of the run body and found the two halves sharing
/// "twelve values and five local event factories", and called the context object that would be
/// needed "a coherent design, but a large change". This is that object, and it is smaller than the
/// measurement suggested because the five factories collapse into one thing rather than travelling
/// beside it.</para>
///
/// <para>Deliberately a class and not a record: <see cref="Artifacts"/> is the run's live list, added
/// to under its own lock by whichever step produced a file, and <see cref="Budget"/> is a counter.
/// A value type here would suggest a snapshot, which is the opposite of what these are.</para>
/// </summary>
public sealed class RunScope
{
    /// <param name="writableRoots">
    /// Folders this workspace was granted in an earlier run, from
    /// <see cref="Enactive.Core.Permissions.WritableRoots"/>. Null for a run that has none and for
    /// every caller that does not deal in them — a test driving the engine is not making a statement
    /// about the machine's policy by leaving it out.
    /// </param>
    public RunScope(
        Guid runId, Guid taskId, RunBudget budget, List<ArtifactRef> artifacts,
        IEnumerable<string>? writableRoots = null)
    {
        RunId = runId;
        TaskId = taskId;
        Budget = budget;
        Artifacts = artifacts;
        Granted = writableRoots is null ? new GrantedRoots() : new GrantedRoots(writableRoots);
    }

    public Guid RunId { get; }
    public Guid TaskId { get; }
    public RunBudget Budget { get; }

    /// <summary>The files this run has produced. Mutated by the steps; read under <c>lock</c>.</summary>
    public List<ArtifactRef> Artifacts { get; }

    /// <summary>
    /// Places outside the workspace this run has been given permission to write to.
    ///
    /// <para>Per RUN rather than per step, which is the whole point of it being here: "yes, this
    /// build may write to C:\out" is answered once and holds for the steps that follow. Per step it
    /// would be the same question five times, and a question asked five times is one nobody reads
    /// by the third.</para>
    ///
    /// <para>What is granted HERE goes no further than the run — see <see cref="GrantedRoots"/>.
    /// What it STARTS from may be older: a folder this workspace was given on an earlier card and
    /// that the person chose to keep.</para>
    /// </summary>
    public GrantedRoots Granted { get; }

    /// <summary>
    /// Any event of this run.
    ///
    /// <para>The step number rides along in the payload so a UI can attribute an event to the right
    /// step card even when several steps are running at once. No schema change needed.</para>
    /// </summary>
    public WorkEvent Ev(EventKind kind, string summary, int? stepNo = null)
        => Event(kind, summary, stepNo is { } n ? $"{{\"step\":{n}}}" : null);

    /// <summary>
    /// An event of this run carrying a payload somebody else built — a plan, a step outcome, an
    /// artifact. Here so that no caller has to assemble a <see cref="WorkEvent"/> by hand and
    /// remember which of its two Guids is the run and which the task: they are adjacent, both Guid,
    /// and getting them the wrong way round produces events that place perfectly and belong to
    /// nothing.
    /// </summary>
    public WorkEvent Event(EventKind kind, string summary, string? payloadJson)
        => new(Guid.NewGuid(), TaskId, RunId, DateTimeOffset.UtcNow, kind, summary, payloadJson);

    /// <summary>
    /// A routing decision, carrying its choice as VALUES and not only as a sentence. The panel that
    /// answers "which model actually ran this" reads the payload, so rewording a summary cannot
    /// change what it shows — the same reason step outcomes stopped being parsed out of prose.
    /// </summary>
    public WorkEvent Route(string purpose, ModelRef reference, string summary,
                           int? stepNo = null, StepComplexity? complexity = null)
        => new(Guid.NewGuid(), TaskId, RunId, DateTimeOffset.UtcNow, EventKind.Routed, summary,
               WorkEventPayload.RoutePayload(purpose, reference.ProviderId, reference.Model,
                                             stepNo, complexity?.ToString().ToLowerInvariant()));

    /// <summary>
    /// Tokens spent OUTSIDE the tool loop, counted against the budget on the way past.
    ///
    /// <para>The loop emits its own usage; planning and review call the provider directly, so their
    /// cost was spent on every run and counted on none — which made the run total execute-only while
    /// the reviewer, on the most expensive model bound, read whole documents for free as far as the
    /// UI was concerned.</para>
    /// </summary>
    /// <param name="cached">
    /// The share of the prompt the provider served from its cache, when it says. Null - not zero -
    /// where it does not, which is every local runtime and every phase before 2026-09-11.
    /// </param>
    public WorkEvent Usage(string purpose, ModelRef reference, int prompt, int completion,
                           int? stepNo = null, int? cached = null, int? created = null)
    {
        Budget.TokensUsed(prompt, completion);
        return new(Guid.NewGuid(), TaskId, RunId, DateTimeOffset.UtcNow, EventKind.UsageReported,
                   $"tokens: {prompt} in, {completion} out"
                   + (cached is > 0 ? $" ({cached} cached)" : "")
                   + $" ({reference.ProviderId}/{reference.Model}, {purpose})",
                   WorkEventPayload.UsagePayload(prompt, completion, stepNo,
                                                 reference.ProviderId, reference.Model, purpose, cached, created));
    }

    /// <summary>
    /// The one place a run ends.
    ///
    /// <para><c>TaskCompleted</c> is emitted for <c>Completed</c> and NOTHING else — the whole point
    /// of the outcome type is that a failure cannot arrive dressed as a success — and the kind
    /// travels in the payload so the UI, the history and the Inbox read a value instead of parsing
    /// the wording.</para>
    /// </summary>
    public WorkEvent Terminal(RunOutcomeKind kind, string? reason, Func<List<ArtifactRef>, string> summarize)
        => new(Guid.NewGuid(), TaskId, RunId, DateTimeOffset.UtcNow,
               kind == RunOutcomeKind.Completed ? EventKind.TaskCompleted : EventKind.TaskFailed,
               kind == RunOutcomeKind.Completed
                   ? summarize(Artifacts)
                   : $"{kind}{(string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason)}",
               WorkEventPayload.OutcomePayload(kind, reason));

    /// <summary>
    /// A criterion's result as VALUES as well as a sentence — the same reason every other event
    /// carries a payload: rewording a summary must not change what a reader of the run sees.
    /// </summary>
    public WorkEvent Criterion(CriterionResult r)
        => new(Guid.NewGuid(), TaskId, RunId, DateTimeOffset.UtcNow, EventKind.CriterionEvaluated,
               r.Describe(),
               WorkEventPayload.CriterionPayload(r.Name, r.Outcome.ToString(), r.Required, r.ExitCode));
}
