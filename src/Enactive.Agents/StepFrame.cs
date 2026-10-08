namespace Enactive.Agents;

using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Enactive.Core.Tools;

/// <summary>
/// The step being worked: which it is, what it has said and done, what it must hand on and what it may change. The tool
/// loop builds it once per step and hands it to the modules that decide about the step's calls and its end -
/// <see cref="CallAdmission"/>, <see cref="StepEnding"/>, <see cref="ToolResultAccounting"/> - instead of handing each
/// of them the same dozen things in a different order.
///
/// <para><b>Why one.</b> The admission took thirty arguments, the step's ending thirteen and the accounting of results
/// nine, most of them these. And each kept what it was handed: after a handover the loop starts the step's record of
/// what it has done over (<see cref="StartOver"/>), and the admission and the ending went on reading the record from
/// before it - a command made before the handover refused as "already run" after it, which the handover exists to
/// allow. They read <see cref="Progress"/> from here, so there is one record and they all read the current one.</para>
///
/// <para><b>What comes from outside.</b> <see cref="Reads"/> and <see cref="Boundary"/> are not helpers of the
/// admission: the reads are the step's, kept by the run's session across the step's attempts and read by the
/// accounting, the hand-over's coverage and the handover; the boundary is what the plan let the step change. Both are
/// handed in, as the journal and the conversation are.</para>
/// </summary>
internal sealed class StepFrame(
    Guid taskId,
    Guid runId,
    int? stepNo,
    WorkspaceInfo workspace,
    IReadOnlyList<ToolDefinition> definitions,
    List<ChatMessage> messages,
    ExecutionJournal journal,
    ReadLedger reads,
    IArtifactScope store,
    StepOutputSchema? outputSchema = null,
    StepOutputSlot? outputSlot = null,
    ToolDefinition? submitTool = null,
    WriteBoundary? boundary = null,
    IWorkspaceChanges? changes = null,
    WorkspaceSnapshot? stepStart = null)
{
    private readonly IReadOnlyList<ToolDefinition> _definitions = definitions;

    public Guid TaskId { get; } = taskId;
    public Guid RunId { get; } = runId;
    public int? StepNo { get; } = stepNo;
    public Guid WorkspaceId { get; } = workspace.Id;
    public string WorkspaceRoot { get; } = workspace.RootPath;

    /// <summary>The step's conversation.</summary>
    public List<ChatMessage> Messages { get; } = messages;

    /// <summary>What the step did, recorded as it happens.</summary>
    public ExecutionJournal Journal { get; } = journal;

    /// <summary>What the step has read - the session's, across the step's attempts.</summary>
    public ReadLedger Reads { get; } = reads;

    /// <summary>The step's view of the artifact store: what it touches is attributed to it and can be undone.</summary>
    public IArtifactScope Store { get; } = store;

    /// <summary>The calls of this step that did not go through and that nothing has made good.</summary>
    public OpenFailures Open { get; } = new(definitions);

    /// <summary>What the step has done in this conversation - its calls, their results, whether it is going round.</summary>
    public StepProgress Progress { get; private set; } = new(definitions);

    /// <summary>What the step must hand on as values, where it is kept, and the hand-over as the run offers it.</summary>
    public StepOutputSchema? OutputSchema { get; } = outputSchema;
    public StepOutputSlot? OutputSlot { get; } = outputSlot;
    public ToolDefinition? SubmitTool { get; } = submitTool;

    /// <summary>What the step may change, and the means to measure what it made itself.</summary>
    public WriteBoundary? Boundary { get; } = boundary;
    public IWorkspaceChanges? Changes { get; } = changes;
    public WorkspaceSnapshot? StepStart { get; } = stepStart;

    /// <summary>
    /// A handover replaced the conversation: the step's record of what it has done starts over, and the reads it can no
    /// longer see are forgotten. A step that had just been told to carry on from its notes and asked where its report
    /// was is not going round in circles; it is in a conversation where it has not asked yet.
    /// </summary>
    public void StartOver()
    {
        Progress = new StepProgress(_definitions);
        Reads.ForgetDiscardedReads();
    }
}
