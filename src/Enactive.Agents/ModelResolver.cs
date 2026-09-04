namespace Enactive.Agents;

using Enactive.Core.Providers;
using Enactive.Core.Workers;

/// <summary>Resolves a worker's model: user override wins, else preferred; fallback on failure.</summary>
public sealed class ModelResolver : IModelResolver
{
    public ModelRef Resolve(ModelPolicy policy) => policy.UserOverride ?? policy.Preferred;

    public ModelRef? NextOnFailure(ModelPolicy policy, ModelRef failed)
        => policy.Fallback is { } fallback && !fallback.Equals(failed) ? fallback : null;
}
