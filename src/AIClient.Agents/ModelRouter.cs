namespace AIClient.Agents;

using AIClient.Core.Providers;
using AIClient.Core.Workers;

/// <summary>
/// Default <see cref="IModelRouter"/>: Execute resolves the worker's own <see cref="ModelPolicy"/>;
/// Plan/Review return their configured binding (or null = off). Empty bindings = single-agent (plan
/// uses the executing model, no review) — so the simple one/two-model setups are just the degenerate case.
/// </summary>
public sealed class ModelRouter : IModelRouter
{
    private readonly IModelResolver _resolver;
    private readonly IReadOnlyDictionary<ModelPurpose, ModelRef> _bindings;

    public ModelRouter(IModelResolver resolver, IReadOnlyDictionary<ModelPurpose, ModelRef>? bindings = null)
    {
        _resolver = resolver;
        _bindings = bindings ?? new Dictionary<ModelPurpose, ModelRef>();
    }

    public ModelRef? Resolve(ModelPurpose purpose, Worker worker)
    {
        if (purpose == ModelPurpose.Execute)
            return _resolver.Resolve(worker.ModelPolicy);
        return _bindings.TryGetValue(purpose, out var model) ? model : null;
    }
}
