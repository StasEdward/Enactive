namespace Enactive.Agents;

using Enactive.Core.Providers;
using Enactive.Core.Workers;

/// <summary>
/// Which models are serving this run, and the providers already created for them.
///
/// <para>Eight loose values in the run body — worker, model, provider, planRef, planProvider,
/// reviewRef, reviewOn, reviewProvider, reviewModel — resolved once at the top and then carried,
/// unchanged, through every branch beneath. Two of them are derived from a third and existed only to
/// spare a null check at the point of use.</para>
///
/// <para>Gathered because the defect log's obstacle to splitting the run body was the number
/// of values the halves share. Half of that count was this: not a design, just a resolution step
/// whose results had nowhere to live.</para>
/// </summary>
/// <param name="Review">
/// The Review binding, or null when none is bound. <c>ReviewOn</c> is this being non-null and
/// nothing else — it used to be a separate bool, which is one more thing that can disagree with the
/// value it summarises.
/// </param>
public sealed record RunModels(
    Worker Worker,
    ModelRef Model,
    IChatProvider Provider,
    ModelRef Plan,
    IChatProvider PlanProvider,
    ModelRef? Review,
    IChatProvider? ReviewProvider)
{
    /// <summary>Whether a reviewer runs at all: a Review model is bound.</summary>
    public bool ReviewOn => Review is not null;

    /// <summary>The review model's name, or empty when none is bound.</summary>
    public string ReviewModel => Review?.Model ?? "";

    /// <summary>Whether the planner runs somewhere other than the executing model.</summary>
    public bool PlanIsElsewhere
        => Plan.ProviderId != Model.ProviderId || Plan.Model != Model.Model;
}
