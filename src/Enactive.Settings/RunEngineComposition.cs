namespace Enactive.Settings;

using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using Enactive.Workspace;

/// <summary>Immutable engine switches captured before a host starts asynchronous run setup.</summary>
public sealed record RunEngineOptions(
    int ReviewRetries, int SuccessRetries, bool ProposeChecks, int? NumCtx,
    GenerationBudgets GenerationBudgets, RepairConsultation RepairConsultation,
    bool DisableThinking, int MaxParallelSteps, int EvidenceBudget, bool AllowImplicitToolCalls,
    bool RevertRejectedSteps, bool StepOutputs = false, bool TypedCriteria = false,
    bool DynamicSteps = false, FanOutLimits? FanOut = null, bool ValidateWaves = false, bool ReportBlocked = false,
    bool SemanticCriteria = false)
{
    public static RunEngineOptions Capture(AppSettings settings) => new(
        settings.ReviewRetries, settings.SuccessRetries, settings.ProposeChecks, settings.NumCtx,
        settings.GenerationBudgets, settings.RepairConsultation, settings.DisableThinking,
        settings.MaxParallelSteps, settings.EvidenceBudget, settings.AllowImplicitToolCalls,
        settings.RevertRejectedSteps, settings.StepOutputs, settings.TypedCriteria,
        settings.DynamicSteps, new FanOutLimits(settings.MaxStepsPerExpansion, settings.MaxTotalSteps, settings.MaxFanOutDepth),
        settings.ValidateWaves, settings.ReportBlocked, settings.SemanticCriteria);
}

/// <summary>Host-owned resources and policy. Their lifetime remains with the caller.</summary>
public sealed record RunEngineResources(
    IChatProviderFactory Providers, IModelResolver Models, IWorkerProvider Workers,
    IToolRegistry Tools, IArtifactStore Artifacts, WorkspaceInfo Workspace, Planner Planner,
    IPermissionEngine Permissions, IDecisionHandler Decisions, PermissionPolicy Policy,
    IServiceProvider Services, IModelRouter Router, OrchestratorServices? Agents = null);

/// <summary>The single application mapping from settings and run resources to an orchestrator.</summary>
public static class RunEngineComposition
{
    public static Orchestrator Build(RunEngineResources resources, RunEngineOptions options,
        IRunCheckpointStore? checkpoints = null, RunSettings? settings = null,
        IReadOnlyList<SuccessCriterionDefinition>? successCriteria = null, ExecutionLimits? limits = null)
        => new(new WorkspaceChangesFactory(), resources.Providers, resources.Models, resources.Workers,
            resources.Tools, resources.Artifacts, resources.Workspace, resources.Planner,
            resources.Permissions, resources.Decisions, resources.Policy, resources.Services,
            router: resources.Router, reviewRetries: options.ReviewRetries, successRetries: options.SuccessRetries,
            proposeChecks: options.ProposeChecks, numCtx: options.NumCtx,
            generationBudgets: options.GenerationBudgets, repairConsultation: options.RepairConsultation,
            disableThinking: options.DisableThinking, maxParallelSteps: options.MaxParallelSteps,
            evidenceBudget: options.EvidenceBudget, allowImplicitToolCalls: options.AllowImplicitToolCalls,
            revertRejectedSteps: options.RevertRejectedSteps, checkpoints: checkpoints, settings: settings,
            successCriteria: successCriteria, limits: limits, agents: resources.Agents,
            // The kinds of project the engine can build for its own "no new build errors" check.
            // A new kind is a new IEcosystem here; nothing in the orchestrator changes.
            ecosystems: [new DotnetEcosystem()],
            stepOutputs: options.StepOutputs, typedCriteria: options.TypedCriteria,
            dynamicSteps: options.DynamicSteps, fanOut: options.FanOut, validateWaves: options.ValidateWaves,
            reportBlocked: options.ReportBlocked, semanticCriteria: options.SemanticCriteria);
}
