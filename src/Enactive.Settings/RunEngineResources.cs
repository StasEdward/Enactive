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

/// <summary>Host-owned resources and policy. Their lifetime remains with the caller.</summary>
public sealed record RunEngineResources(
    IChatProviderFactory Providers, IModelResolver Models, IWorkerProvider Workers,
    IToolRegistry Tools, IArtifactStore Artifacts, WorkspaceInfo Workspace, Planner Planner,
    IPermissionEngine Permissions, IDecisionHandler Decisions, PermissionPolicy Policy,
    IServiceProvider Services, IModelRouter Router, OrchestratorServices? Agents = null);
