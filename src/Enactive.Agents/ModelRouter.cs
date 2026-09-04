namespace Enactive.Agents;

using Enactive.Core.Providers;
using Enactive.Core.Tasks;
using Enactive.Core.Workers;

/// <summary>
/// Default <see cref="IModelRouter"/>. Plan/Review return their configured binding (or null = off). Execute
/// resolves the worker's own <see cref="ModelPolicy"/> by default, but per-step auto-routing sends Trivial
/// steps to <c>executeLight</c> and Complex steps to <c>executeHeavy</c> when those are configured. Empty
/// bindings and no light/heavy = single model everywhere — the simple setups are the degenerate case.
/// </summary>
public sealed class ModelRouter : IModelRouter
{
    private readonly IModelResolver _resolver;
    private readonly IReadOnlyDictionary<ModelPurpose, ModelRef> _bindings;
    private readonly ModelRef? _executeLight;
    private readonly ModelRef? _executeHeavy;

    public ModelRouter(
        IModelResolver resolver,
        IReadOnlyDictionary<ModelPurpose, ModelRef>? bindings = null,
        ModelRef? executeLight = null,
        ModelRef? executeHeavy = null)
    {
        _resolver = resolver;
        _bindings = bindings ?? new Dictionary<ModelPurpose, ModelRef>();
        _executeLight = executeLight;
        _executeHeavy = executeHeavy;
    }

    public ModelRef? Resolve(ModelPurpose purpose, Worker worker)
    {
        if (purpose == ModelPurpose.Execute)
            return _resolver.Resolve(worker.ModelPolicy);
        return _bindings.TryGetValue(purpose, out var model) ? model : null;
    }

    public ModelRef? ResolveExecute(Worker worker, StepComplexity complexity)
    {
        var baseModel = _resolver.Resolve(worker.ModelPolicy);
        return complexity switch
        {
            StepComplexity.Trivial => _executeLight ?? baseModel,
            StepComplexity.Complex => _executeHeavy ?? baseModel,
            _ => baseModel
        };
    }
}
