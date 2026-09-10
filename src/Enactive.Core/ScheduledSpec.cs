namespace Enactive.Core.Schedules;

using Enactive.Core.Context;
using Enactive.Core.Templates;

/// <summary>What a schedule turned into, or why it could not.</summary>
/// <param name="Why">
/// Empty when it resolved. Otherwise the sentence a person needs: nobody is at the machine at 3am,
/// so a schedule that cannot produce a task has to leave behind something better than nothing
/// having happened.
/// </param>
public sealed record ScheduledSpecResult(ResolvedTaskSpec? Spec, string Why)
{
    public static ScheduledSpecResult Ok(ResolvedTaskSpec spec) => new(spec, "");
    public static ScheduledSpecResult No(string why) => new(null, why);
}

/// <summary>
/// Turns a schedule into the thing a run starts from.
///
/// <para>Here rather than in the host for the usual reason - a host is where a rule goes to stop
/// being tested - and because the two kinds of work resolve differently on purpose:</para>
///
/// <list type="bullet">
/// <item><b>A template</b> is resolved NOW, against the template as it stands today. Editing a
/// template is meant to change what tomorrow's scheduled run does; freezing it at the moment the
/// schedule was made would produce a schedule that quietly ignores its own template.</item>
/// <item><b>A past run</b> is not resolved at all - its <see cref="ResolvedTaskSpec"/> was frozen
/// when it ran and is used as it stands. "Run that again" means the thing that ran.</item>
/// </list>
///
/// <para>The price of the first is that a template edited into needing a parameter this schedule
/// does not supply becomes unresolvable. That is reported, in words, rather than skipped - the same
/// rule the console already follows when a person asks for a template it cannot run unattended.</para>
/// </summary>
public static class ScheduledSpec
{
    /// <param name="findTemplate">
    /// How to look a template up. A delegate because the template store lives a layer out, and Core
    /// is not going to reach for it.
    /// </param>
    public static ScheduledSpecResult For(
        Schedule schedule, WorkspaceInfo workspace, Func<string, TaskTemplate?> findTemplate)
    {
        if (!schedule.Work.IsValid)
            return ScheduledSpecResult.No(
                "this schedule names neither a template nor a past run, so there is nothing to start");

        if (schedule.Work.SpecSnapshot is { Length: > 0 } snapshot)
            return ResolvedTaskSpec.Parse(snapshot) is { } frozen
                ? ScheduledSpecResult.Ok(frozen)
                : ScheduledSpecResult.No(
                    "the recorded task this schedule repeats could not be read back - it was written by a "
                    + "different version, or the record is damaged");

        var id = schedule.Work.TemplateId!;
        if (findTemplate(id) is not { } template)
            return ScheduledSpecResult.No(
                $"there is no template '{id}' in this workspace or the global library any more");

        var resolution = TemplateResolution.Resolve(template, workspace, schedule.Permissions, schedule.Work.Parameters);
        if (resolution.Spec is { } spec)
            return ScheduledSpecResult.Ok(spec);

        // Almost always a parameter added to the template since the schedule was made. Nobody is
        // here to answer it, so the honest outcome is a report rather than a half-filled task.
        return ScheduledSpecResult.No(
            $"'{template.Name}' cannot run unattended as this schedule has it: "
            + string.Join("; ", resolution.Problems.Select(p => p.ToString()))
            + ". Give the parameter a default in the template, or edit the schedule to supply it.");
    }
}
