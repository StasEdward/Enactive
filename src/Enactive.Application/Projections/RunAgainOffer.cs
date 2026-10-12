namespace Enactive.Application.Projections;

using System.Globalization;
using Enactive.Core.Templates;

/// <summary>
/// Whether a past run's "Run again" is offered, and what its tooltip says. Run again re-resolves the template as it stands
/// NOW, so it is offered only while the template is still in the library: one deleted or renamed has no current version,
/// and a button that fails when pressed is worse than one that is not there.
///
/// <para>Worked out in the window's past-run view model until the service's past run needed the same button; a second
/// copy on the service would have been the one to offer Run again for a template that is gone.</para>
/// </summary>
internal sealed record RunAgainOffer(bool Can, string Tooltip)
{
    /// <param name="spec">The specification the run was started from; null for a typed run.</param>
    /// <param name="current">The template as it is in the library now; null when it is not there.</param>
    public static RunAgainOffer Of(ResolvedTaskSpec? spec, TaskTemplate? current)
        => spec is null ? new(false, "This run did not come from a template.")
            : current is null ? new(false, $"Template '{spec.TemplateId}' is no longer in this library.")
            : new(true, string.Create(CultureInfo.InvariantCulture,
                $"Re-resolve '{current.Name}' v{current.Version} — this run used v{spec.TemplateVersion}."));
}

/// <summary>
/// The tabs of a past run, as the window has them: Execution, Artifacts, Timeline - and which one it opens on.
/// </summary>
internal static class PastRunTabs
{
    public const int Execution = 0;
    public const int Artifacts = 1;
    public const int Timeline = 2;

    /// <summary>
    /// A record that rebuilds no step cards opens on its timeline, which is what such a record holds: on Execution it was
    /// an empty tab that read as a run that did nothing. Why there are none is Core's answer (RunReport.WhyNoSteps).
    /// </summary>
    public static int Opening(int steps) => steps == 0 ? Timeline : Execution;
}
