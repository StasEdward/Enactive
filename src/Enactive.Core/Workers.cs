namespace Enactive.Core.Workers;

using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Tasks;

/// <summary>Model selection policy for a worker (PLAN_v2 §2.5): preferred / fallback / user override.</summary>
public sealed record ModelPolicy(
    ModelRef Preferred,
    ModelRef? Fallback = null,
    ModelRef? UserOverride = null);

/// <summary>A role (not a model): instructions + tools + permissions + model policy.</summary>
public sealed record Worker(
    string Id,
    string Role,
    string Instructions,
    IReadOnlyList<string> ToolAllowlist,
    PermissionLevel DefaultLevel,
    ModelPolicy ModelPolicy);

/// <summary>Resolves which concrete model to use, and what to fall back to on failure.</summary>
public interface IModelResolver
{
    ModelRef Resolve(ModelPolicy policy);
    ModelRef? NextOnFailure(ModelPolicy policy, ModelRef failed);
}

/// <summary>Supplies the available worker roles and resolves one by id.</summary>
public interface IWorkerProvider
{
    Worker Default { get; }
    IReadOnlyList<Worker> All { get; }

    /// <summary>Returns the worker with this id, or <see cref="Default"/> when id is null/unknown.</summary>
    Worker Get(string? id);
}

/// <summary>A phase of a run that a model is chosen for (PLAN_v2 team-of-models, Docs/MODELS.md).</summary>
public enum ModelPurpose { Plan, Review, Execute }

/// <summary>
/// Resolves which model runs a given phase for a given worker. Execute uses the worker's own model;
/// Plan/Review use configured bindings (null = the phase is off / falls back to the executing model).
/// This replaces the old fixed "reasoner" so any number of providers/models can be assigned per phase.
/// </summary>
public interface IModelRouter
{
    ModelRef? Resolve(ModelPurpose purpose, Worker worker);

    /// <summary>
    /// The Execute model for a step of the given complexity: Trivial/Complex map to the configured light/heavy
    /// models when set, otherwise (and always for Normal) the worker's own model.
    /// </summary>
    ModelRef? ResolveExecute(Worker worker, StepComplexity complexity);
}
