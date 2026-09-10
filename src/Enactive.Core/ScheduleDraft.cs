namespace Enactive.Core.Schedules;

using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;

/// <summary>
/// What a person has filled in, before anything has been saved.
///
/// <para>A separate type from <see cref="Schedule"/> on purpose. A Schedule is a thing that RUNS,
/// and every one that exists should be one that can; a draft is allowed to be half-finished, wrong,
/// or contradictory, because that is what a form is while somebody is typing into it.</para>
/// </summary>
/// <param name="Id">
/// The schedule being edited, or null for a new one. Editing keeps the ID - the outcomes already
/// filed against it are its history, and a new id would silently start that history again.
/// </param>
public sealed record ScheduleDraft(
    string Name,
    string WorkspaceRoot,
    ScheduledWork Work,
    ScheduleTiming Timing,
    PermissionPolicy Permissions,
    MissedRun Missed = MissedRun.Skip,
    bool Enabled = true,
    Guid? Id = null);

/// <param name="Schedule">The thing to save, or null when it cannot be saved.</param>
/// <param name="Problems">
/// Empty when it can. Otherwise every reason, not the first one: a form that reveals its objections
/// one at a time makes a person submit four times to learn four things.
/// </param>
public sealed record ScheduleDraftResult(Schedule? Schedule, IReadOnlyList<string> Problems);

/// <summary>
/// Whether a draft can be saved, and what to say when it cannot.
///
/// <para><b>The point of this is that the window and the runner agree.</b> A form with its own idea
/// of what is valid accepts schedules the runner then refuses at three in the morning, and the
/// person finds out days later from an Inbox item — having been told, when they saved it, that
/// everything was fine. So the template half of this check is not written here: it CALLS
/// <see cref="ScheduledSpec.For"/>, which is what the runner calls. One resolution, checked twice,
/// rather than two resolutions that agree until somebody edits one.</para>
///
/// <para>The timing half is here because the runner never sees it — an unfireable timing produces no
/// occurrence, so the runner has nothing to complain about and silently never runs. That is the
/// worst of the failures and the one nobody notices.</para>
/// </summary>
public static class ScheduleDrafts
{
    /// <param name="findTemplate">Same delegate <see cref="ScheduledSpec.For"/> takes, for the same reason.</param>
    public static ScheduleDraftResult Check(
        ScheduleDraft draft, WorkspaceInfo workspace, DateTimeOffset now,
        Func<string, TaskTemplate?> findTemplate)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(draft.Name))
            problems.Add("Give it a name. It is what you will see in the Inbox when it reports back.");

        if (string.IsNullOrWhiteSpace(draft.WorkspaceRoot))
            problems.Add("A schedule runs in a workspace, and this one names none.");

        if (!draft.Work.IsValid)
            problems.Add(
                draft.Work.TemplateId is { Length: > 0 } && draft.Work.SpecSnapshot is { Length: > 0 }
                    ? "Choose a template or a past run — not both."
                    : "Choose what to run: a template, or a past run to repeat.");

        problems.AddRange(TimingProblems(draft.Timing, now));

        var candidate = new Schedule(
            draft.Id ?? Guid.NewGuid(), draft.WorkspaceRoot, draft.Name.Trim(),
            draft.Work, draft.Timing, draft.Permissions,
            CreatedAt: now, Missed: draft.Missed, Enabled: draft.Enabled);

        // The runner's own resolution, run now. Only when the rest holds: reporting "the template
        // needs a parameter" on top of "choose what to run" is noise about a choice not yet made.
        if (problems.Count == 0)
        {
            var resolved = ScheduledSpec.For(candidate, workspace, findTemplate);
            if (resolved.Spec is null)
                problems.Add(Sentence(resolved.Why));
        }

        return problems.Count == 0
            ? new ScheduleDraftResult(candidate, Array.Empty<string>())
            : new ScheduleDraftResult(null, problems);
    }

    private static IEnumerable<string> TimingProblems(ScheduleTiming timing, DateTimeOffset now)
    {
        if (ScheduleClock.Zone(timing.TimeZoneId) is null)
            yield return $"This machine does not know the time zone '{timing.TimeZoneId}'.";

        switch (timing.Repeat)
        {
            case ScheduleRepeat.Weekly when timing.OnDay is null:
                yield return "Choose which day of the week it runs.";
                break;

            case ScheduleRepeat.Once when timing.OnceAt is null:
                yield return "Choose the date and time it runs.";
                break;

            // Saved and never fired is the quietest way for this to go wrong: there is no error, no
            // Inbox item and no run - just a schedule sitting in the list looking set.
            case ScheduleRepeat.Once when timing.OnceAt <= now:
                yield return "That moment has already passed, so it would never run.";
                break;
        }
    }

    /// <summary>
    /// The resolver's reasons are written as clause fragments, to be read after "could not run
    /// because". In a list of objections they are read on their own, so they start like sentences.
    /// </summary>
    private static string Sentence(string why)
        => string.IsNullOrEmpty(why) ? why : char.ToUpperInvariant(why[0]) + why[1..] + ".";
}
