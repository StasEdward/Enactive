namespace AIClient.Core.Workers;

using AIClient.Core.Permissions;
using AIClient.Core.Providers;

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

/// <summary>Supplies workers. The first slice exposes a single default worker.</summary>
public interface IWorkerProvider
{
    Worker Default { get; }
}
