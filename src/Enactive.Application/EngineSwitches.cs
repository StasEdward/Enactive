namespace Enactive.Application;

using Enactive.Agents;
using Enactive.Core.Execution;
using Enactive.Settings;

/// <summary>
/// The engine's switches as a person edits them - numbers as the text typed into a box - and the rules that turn that
/// text back into the settings' engine section. Shared by the window's settings and the service's settings page: the
/// same box, typed the same wrong way, must not mean the engine's default in one host and zero in the other.
/// </summary>
internal sealed class EngineSwitches
{
    public string NumCtx { get; set; } = "";
    public string GenerationAction { get; set; } = "";
    public string GenerationFileWrite { get; set; } = "";
    public string GenerationFinalAnswer { get; set; } = "";
    public string GenerationHandover { get; set; } = "";
    public string GenerationPlanner { get; set; } = "";
    /// <summary>"provider/model", read as every model reference in the settings is (AppSettings.ParseRef).</summary>
    public string RepairModel { get; set; } = "";
    public string RepairThreshold { get; set; } = "0";
    public bool DisableThinking { get; set; }
    public bool AllowImplicitToolCalls { get; set; }
    public string ReviewRetries { get; set; } = "";
    public bool RevertRejectedSteps { get; set; }
    public string MaxParallelSteps { get; set; } = "";
    public string EvidenceBudget { get; set; } = "";

    /// <summary>The switches as they are, for a person to edit.</summary>
    public static EngineSwitches Of(EngineOptions engine) => new()
    {
        NumCtx = engine.NumCtx?.ToString() ?? "",
        GenerationAction = engine.GenerationBudgets.Action.ToString(),
        GenerationFileWrite = engine.GenerationBudgets.FileWrite.ToString(),
        GenerationFinalAnswer = engine.GenerationBudgets.FinalAnswer.ToString(),
        GenerationHandover = engine.GenerationBudgets.Handover.ToString(),
        GenerationPlanner = engine.GenerationBudgets.Planner.ToString(),
        RepairModel = engine.RepairConsultation.Model is { } model ? model.ProviderId + "/" + model.Model : "",
        RepairThreshold = engine.RepairConsultation.FailedRepairs.ToString(),
        DisableThinking = engine.DisableThinking,
        AllowImplicitToolCalls = engine.AllowImplicitToolCalls,
        ReviewRetries = engine.ReviewRetries.ToString(),
        RevertRejectedSteps = engine.RevertRejectedSteps,
        MaxParallelSteps = engine.MaxParallelSteps.ToString(),
        // Blank is automatic (EngineOptions.EvidenceBudget): the box shows a number only where a person set one.
        EvidenceBudget = engine.EvidenceBudget?.ToString() ?? ""
    };

    /// <summary>
    /// <paramref name="engine"/> with these switches. What does not parse falls back to the engine's own default
    /// (EngineOptions.Default) - a number typed wrong is not a choice, and the defaults are written in one place. The
    /// switches not edited here (the experimental ones) are kept as they are.
    /// </summary>
    public EngineOptions ApplyTo(EngineOptions engine)
    {
        var engineDefault = EngineOptions.Default;
        var budgets = engineDefault.GenerationBudgets;
        return engine with
        {
            NumCtx = int.TryParse(NumCtx.Trim(), out var n) ? n : null,
            RepairConsultation = engine.RepairConsultation with
            {
                Model = AppSettings.ParseRef(RepairModel.Trim()),
                // Fewer than two failed repairs is not a pattern worth a stronger model's time: 0 is "never".
                FailedRepairs = NumberOr(RepairThreshold, 0, v => v >= 2)
            },
            GenerationBudgets = new(
                Action: PositiveOr(GenerationAction, budgets.Action),
                FileWrite: PositiveOr(GenerationFileWrite, budgets.FileWrite),
                FinalAnswer: PositiveOr(GenerationFinalAnswer, budgets.FinalAnswer),
                Handover: PositiveOr(GenerationHandover, budgets.Handover),
                Planner: PositiveOr(GenerationPlanner, budgets.Planner)),
            DisableThinking = DisableThinking,
            AllowImplicitToolCalls = AllowImplicitToolCalls,
            // Clamped here as well as in the orchestrator: what is saved should be what will be used, or the settings
            // show one number while the engine quietly runs another.
            ReviewRetries = Math.Clamp(NumberOr(ReviewRetries, engineDefault.ReviewRetries, _ => true), 0, 5),
            RevertRejectedSteps = RevertRejectedSteps,
            MaxParallelSteps = PositiveOr(MaxParallelSteps, engineDefault.MaxParallelSteps),
            // Blank is automatic - the budget follows each reviewer's window (ReviewEvidence) - and so is anything that is
            // not a number: a number typed wrong is not a choice. A number below the floor is raised to it: somebody who
            // typed 1000 wanted LESS, and read as automatic it could have given them up to 72,000. Raised, not kept: an
            // evidence block too small to hold its header is not a smaller setting, it is a reviewer shown nothing.
            EvidenceBudget = int.TryParse(EvidenceBudget.Trim(), out var evidence)
                ? Math.Max(ExecutionJournal.MinimumBudget, evidence) : null
        };
    }

    /// <summary>
    /// What a box that holds a number says: the number, or the fallback when it is not one, or not one the rule
    /// accepts.
    /// </summary>
    public static int NumberOr(string text, int fallback, Func<int, bool> accepts)
        => int.TryParse(text.Trim(), out var value) && accepts(value) ? value : fallback;

    public static int PositiveOr(string text, int fallback) => NumberOr(text, fallback, v => v > 0);
}
