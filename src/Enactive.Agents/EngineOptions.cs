namespace Enactive.Agents;

using Enactive.Core.Chat;
using Enactive.Core.Execution;

/// <summary>
/// The engine's switches: how it plans, works, reviews and checks - everything a person sets once in the
/// settings, as opposed to what belongs to one run (its template's checks and limits, its checkpoint, the
/// settings it was started under).
///
/// <para><b>Why one value.</b> Each switch used to be a parameter of the orchestrator's constructor, with its
/// own default there, a field of a second options record, a line in the settings capture and a line in the
/// mapping between them - five edits for one switch, with two sets of defaults that had already drifted
/// (thinking was off by default in the product and on in the engine). Now a switch is a property here,
/// its product default is in <see cref="Default"/>, and the settings read it from there.</para>
///
/// <para><b>Why no defaults on the constructor.</b> So that adding a property breaks every place that builds
/// one from scratch - the settings capture first of all - at compile time, instead of quietly leaving the new
/// switch at a default nobody chose. Everything else starts from <see cref="Default"/> and changes what it means
/// to with <c>with</c>.</para>
/// </summary>
/// <param name="ReviewRetries">How many times a rejected step may be redone.</param>
/// <param name="SuccessRetries">How many times a run whose checks failed may try to make them pass.</param>
/// <param name="MaxLoadedToolsPerStep">How many tools of its catalog one step may load (ToolBudget).</param>
/// <param name="ProposeChecks">Whether the planner may propose the checks a run is judged by.</param>
/// <param name="NumCtx">The context window asked of a local model, when one is set.</param>
/// <param name="DisableThinking">Send think:false to a local model, skipping its &lt;think&gt; phase.</param>
/// <param name="MaxParallelSteps">How many independent steps may run at once; 1 is one at a time on one conversation.</param>
/// <param name="EvidenceBudget">How much of a step's record its review is shown.</param>
/// <param name="AllowImplicitToolCalls">Execute a call a model described in text instead of making it. Off: that is a way to talk the agent into acting.</param>
/// <param name="RevertRejectedSteps">Put back what a rejected step changed. On: a gate that stops the report but leaves the rejected work on disk is the state a person is most likely to pick up and use.</param>
/// <param name="GenerationBudgets">How many tokens each kind of request may generate.</param>
/// <param name="RepairConsultation">Whether, and how, a stronger model is consulted when a step keeps failing.</param>
/// <param name="StepOutputs">Phase 2: the planner may declare what a step hands on as values, and such a step must hand it on with submit_step_output. Off - it changes the planner's prompt and what every such step must do to finish, and is to be switched on by evidence.</param>
/// <param name="TypedCriteria">Phase 3: the planner may state acceptance criteria as types the engine checks itself. Off: it changes the planner's prompt, and is to be switched on by evidence.</param>
/// <param name="DynamicSteps">Phase 5.3: a step may be declared "for each" item another step hands on, and the plan grow by one step per item. Off, like the phases before it; it needs step outputs.</param>
/// <param name="FanOut">Phase 5.4: how far a plan may grow without asking.</param>
/// <param name="ValidateWaves">Phase 6: a plan's waves are validated where nothing is running, and a regression attributed to the step that made it. Off: it runs builds the run did not run before.</param>
/// <param name="ReportBlocked">Phase 7.2: a step may say it cannot go on (report_blocked). Advisory; the engine's own detection of blocks does not depend on it. Off: it is one more tool offered.</param>
/// <param name="SemanticCriteria">Phase 1.4: the planner may give a step semantic criteria, and a step that has them is judged against those only. Off: it changes what a step's review is.</param>
public sealed record EngineOptions(
    int ReviewRetries,
    int SuccessRetries,
    int MaxLoadedToolsPerStep,
    bool ProposeChecks,
    int? NumCtx,
    bool DisableThinking,
    int MaxParallelSteps,
    int EvidenceBudget,
    bool AllowImplicitToolCalls,
    bool RevertRejectedSteps,
    GenerationBudgets GenerationBudgets,
    RepairConsultation RepairConsultation,
    bool StepOutputs,
    bool TypedCriteria,
    bool DynamicSteps,
    FanOutLimits FanOut,
    bool ValidateWaves,
    bool ReportBlocked,
    bool SemanticCriteria)
{
    /// <summary>The product's defaults: what a new installation runs with, and what the settings start from.</summary>
    public static EngineOptions Default { get; } = new(
        ReviewRetries: 1,
        SuccessRetries: 1,
        MaxLoadedToolsPerStep: ToolBudget.DefaultMaxLoaded,
        ProposeChecks: true,
        NumCtx: null,
        DisableThinking: true,
        MaxParallelSteps: 1,
        EvidenceBudget: ExecutionJournal.DefaultBudget,
        AllowImplicitToolCalls: false,
        RevertRejectedSteps: true,
        GenerationBudgets: new(),
        RepairConsultation: new(),
        StepOutputs: false,
        TypedCriteria: false,
        DynamicSteps: false,
        FanOut: FanOutLimits.Default,
        ValidateWaves: false,
        ReportBlocked: false,
        SemanticCriteria: false);
}
