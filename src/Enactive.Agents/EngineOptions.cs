namespace Enactive.Agents;

using Enactive.Core.Chat;
using Enactive.Core.Execution;

/// <summary>
/// The engine's switches: how it plans, works, reviews and checks - everything a person sets once in the settings,
/// as opposed to what belongs to one run (its template's checks and limits, its checkpoint, the settings it was
/// started under). The settings hold this very object (AppSettings.Engine, "Engine" in settings.json), and the
/// orchestrator is handed it as it is.
///
/// <para><b>One declaration per switch.</b> A switch is ONE property here, and its initializer is its default. It
/// used to be six things kept in step by hand: a constructor parameter with a default of its own, a field of an
/// options record, a line in the settings capture, a line in the mapping, a settings property with another default,
/// and a line in the settings' copy - and the two sets of defaults had drifted (thinking was off by default in the
/// product and on in the engine). There is no constructor with parameters on purpose: a file written before a switch
/// existed is read without it, and the switch keeps the default written here - no migration, no second default.</para>
/// </summary>
public sealed record EngineOptions
{
    /// <summary>The product's defaults: what a new installation runs with.</summary>
    public static EngineOptions Default { get; } = new();

    /// <summary>The context window asked of a local Ollama model, when one is set.</summary>
    public int? NumCtx { get; init; }

    /// <summary>How many tokens each kind of request may generate.</summary>
    public GenerationBudgets GenerationBudgets { get; init; } = new();

    /// <summary>Whether, and how, a stronger model is consulted when a step keeps failing to repair a check.</summary>
    public RepairConsultation RepairConsultation { get; init; } = new();

    /// <summary>
    /// Send think:false to the local model so a reasoning model (qwen3, ...) answers directly instead of burning a
    /// whole turn in &lt;think&gt; with empty content. On by default; only OllamaNative honors it.
    /// </summary>
    public bool DisableThinking { get; init; } = true;

    /// <summary>
    /// Execute a tool call the model only DESCRIBED in its reply (a ```json block) instead of invoking it. Off by default
    /// and deliberately so: a parser cannot tell an intended call from a quoted example, which means anything that can
    /// put text in front of the model can put an action in front of the engine. Turn it on only for a weak local model
    /// that cannot emit structured tool calls at all.
    /// </summary>
    public bool AllowImplicitToolCalls { get; init; }

    /// <summary>
    /// How many times a rejected step may be redone before the run gives up. 1 means two tries in total, which is what
    /// the engine did when this number was hard-coded. It is the dial between "the reviewer's feedback gets used" and
    /// "a weak model burns the budget arguing with a strong one": in a real run one step needed exactly two attempts and
    /// passed, while another used both and was still wrong. Clamped to 0..5 by the orchestrator.
    /// </summary>
    public int ReviewRetries { get; init; } = 1;

    /// <summary>
    /// How many tools of its catalog one step may load. The tools of connected MCP servers are not sent with every
    /// request: they are named in a catalog and loaded by name when the work needs them, and each one loaded is a
    /// definition sent with every later turn of that step. Clamped to 1..32 by the orchestrator.
    /// </summary>
    public int MaxLoadedToolsPerStep { get; init; } = ToolBudget.DefaultMaxLoaded;

    /// <summary>
    /// How many times a run whose success CRITERIA failed may try to make them pass. A criterion is the one thing in a
    /// run that is not somebody's opinion, and until 2026-09-08 a failed one just ended the run: a build left broken was
    /// reported as broken and nothing tried to fix it. 1 gives the agent one attempt with the check's own output in
    /// front of it; the criteria are then re-run and they alone decide. 0 restores the old behaviour - check once, and
    /// stop. Clamped to 0..5 by the orchestrator.
    /// </summary>
    public int SuccessRetries { get; init; } = 1;

    /// <summary>
    /// Ask the planner, before any of the work, for commands that would PROVE the request was carried out - and judge
    /// the run by them when it was given no criteria of its own.
    ///
    /// <para>Until 2026-09-21 the one guard that looks at the WORKSPACE instead of the transcript was reachable only
    /// through a template, so every ad-hoc run was judged on text a model wrote about its own work. A template's
    /// criteria still win outright and the planner is not even asked. Safe to leave on: a proposed check can only make a
    /// verdict stricter (SuccessReport.Apply never promotes), and only by RUNNING and failing - one the shell would not
    /// start, or the policy forbids, reports Unknown and holds nothing back, because nobody asked for it.</para>
    /// </summary>
    public bool ProposeChecks { get; init; } = true;

    /// <summary>
    /// Phase 2: a planned step may declare what it hands on as values, and must then hand it on with submit_step_output;
    /// the steps after it receive the values instead of a retelling. Off until runs show it helps - it changes the
    /// planner's prompt and what a declared step needs to finish.
    /// </summary>
    public bool StepOutputs { get; init; }

    /// <summary>
    /// Phase 3: the planner may state acceptance criteria as types the engine checks itself - file_exists,
    /// file_contains, tests_pass - added to, never instead of, the run's own criteria. Off until runs show it helps.
    /// </summary>
    public bool TypedCriteria { get; init; }

    /// <summary>
    /// Phase 5.3: a step may be declared "for each" item an earlier step hands on, and the plan grows by one step per
    /// item once the list exists. Needs StepOutputs. Off until runs show it helps.
    /// </summary>
    public bool DynamicSteps { get; init; }

    /// <summary>Phase 5.4: how far a plan may grow without asking - per "for each", in total, and in depth.</summary>
    public FanOutLimits FanOut { get; init; } = FanOutLimits.Default;

    /// <summary>
    /// Phase 6: a plan's steps are built and tested once per wave - where nothing is running - instead of only at the
    /// end, and a regression is put on the step that made it, or said to be ambiguous. Off until runs show it helps: it
    /// runs builds, and trial builds when something broke.
    /// </summary>
    public bool ValidateWaves { get; init; }

    /// <summary>
    /// Phase 7.2: a step may say it cannot go on (report_blocked) - advisory; the engine finds the blocks it can see
    /// without it. Off until runs show a local model uses it for real blocks and not for hard work.
    /// </summary>
    public bool ReportBlocked { get; init; }

    /// <summary>
    /// Phase 1.4: the planner may set a step semantic criteria (needs TypedCriteria), and a step that has them is judged
    /// against those alone, each verdict citing evidence of the kinds the criterion allows. Off until runs show it.
    /// </summary>
    public bool SemanticCriteria { get; init; }

    /// <summary>
    /// Put a rejected step's files back to how they were before it ran. Without this the gate stops only the REPORT:
    /// the run says Failed while the rejected document stays in the workspace, which is the version someone is most
    /// likely to open next. A file changed since the step wrote it is left alone and named in the log. Turn it off to
    /// inspect what a rejected step actually produced.
    /// </summary>
    public bool RevertRejectedSteps { get; init; } = true;

    /// <summary>
    /// How many independent plan steps may run at once. 1 = the original behaviour: one step at a time on one shared
    /// conversation. Above 1 each concurrent step gets its own forked conversation, seeded with a digest of what earlier
    /// steps concluded. Only pays off when steps route to different providers - two steps on one Ollama still queue on
    /// the GPU.
    /// </summary>
    public int MaxParallelSteps { get; init; } = 1;

    /// <summary>
    /// How many characters of tool evidence the reviewer is shown, shared between every call the step made. Raise it
    /// for work that reads many files: the budget is divided, so thirteen reads under the default leave about 320
    /// characters of each - too little to check anything quoted from one.
    /// </summary>
    public int EvidenceBudget { get; init; } = ExecutionJournal.DefaultBudget;
}
