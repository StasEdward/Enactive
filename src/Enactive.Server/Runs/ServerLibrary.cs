namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Schedules;
using Enactive.Core.Templates;
using Enactive.Server.Accounts;
using Enactive.Settings;
using Enactive.Workspace;

/// <summary>
/// What the template editor opens (TemplateEditViewModel's arguments): the draft, whether its id may still change, the
/// libraries it may be saved to - the first is the one offered - and the tools the service's engine registers, for the
/// deny and ask lists.
/// </summary>
internal sealed record TemplateDraft(TaskTemplate Template, bool IdEditable, IReadOnlyList<TemplateScope> Scopes, IReadOnlyList<string> ToolNames);

/// <summary>
/// A workspace's templates and schedules, kept from the browser by Operators (ADR 0002 §4: an Operator "edits
/// templates and schedules"; server plan phase 3).
///
/// <para><b>The rules are the window's, not copies of them.</b> A template is checked by TemplateValidator and written
/// by TemplateStore in its own format; a schedule is checked by ScheduleDrafts - which resolves its task the way the
/// scheduler will at its hour (ScheduledSpec) - and its permissions are the tier with the settings' shell rule on top
/// (SchedulePolicy), asked of the settings as they are now. A form with its own idea of valid accepts schedules the
/// scheduler then refuses at three in the morning.</para>
///
/// <para>Only the workspaces the service knows; the role checked at each call; every change audited.</para>
/// </summary>
internal sealed class ServerLibrary(UserStore users, ServerEngine engine, IRunControl runs)
{
    /// <summary>
    /// The library as the window shows it (TemplatesViewModel): this workspace's templates, the service account's global
    /// ones and the built-ins, each run's summary said against the folder's level as the service has it now - a template
    /// may only narrow what its workspace allows, so "runs at Execute" is a statement about this folder.
    /// </summary>
    public (TemplatesViewModel? Library, string? Problem) Library(ClaimsPrincipal user, string root)
        => Reading(user, root) is { } known
            ? (new TemplatesViewModel(known.RootPath, PolicyOf(known)), null)
            : (null, Why(user, Role.Viewer));

    /// <summary>
    /// What the editor opens - a template's own (<paramref name="copy"/> false), a copy of it under an id nothing uses, or a
    /// new one (<paramref name="id"/> empty) - by the window's rules (TemplateDrafts). An Operator's: a draft is for saving.
    /// </summary>
    public (TemplateDraft? Draft, string? Problem) Draft(ClaimsPrincipal user, string root, string? id, bool copy)
    {
        if (Writing(user, root) is not { } known)
            return (null, Why(user, Role.Operator));
        var inventory = new TemplateStore(known.RootPath).Inventory();
        var tools = ToolNames();
        if (string.IsNullOrWhiteSpace(id))
            return (new TemplateDraft(TemplateDrafts.New(), true, Scopes(null), tools), null);
        if (inventory.FirstOrDefault(t => string.Equals(t.Template.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } entry)
            return (null, $"There is no template '{id}' here.");
        if (copy)
            return (new TemplateDraft(TemplateDrafts.Duplicate(entry, inventory.Select(t => t.Template.Id)), true, Scopes(null), tools), null);
        var (draft, idEditable) = TemplateDrafts.Edit(entry);
        return (new TemplateDraft(draft, idEditable, Scopes(entry.Scope), tools), null);
    }

    /// <summary>
    /// Starts a template from the library, as the window's library does (TemplatesWindow.OnRun): resolved with the answers
    /// given - again here, from the template as its file holds it now and the folder's level now, never from what the page
    /// last showed - and started as that saved task through the runs' one door, which checks the person again. The run,
    /// or why there is none.
    /// </summary>
    public (Guid? Run, string Said) RunTemplate(ClaimsPrincipal user, string root, string id, IReadOnlyDictionary<string, string> answers)
    {
        if (Writing(user, root) is not { } known)
            return (null, Why(user, Role.Operator, "starting runs"));
        if (new TemplateStore(known.RootPath).Find(id) is not { } template)
            return (null, $"There is no template '{id}' here.");

        var resolved = TemplateResolution.Resolve(template, WorkspaceInfo.For(known.RootPath), PolicyOf(known), answers);
        if (resolved.Spec is not { } spec)
            return (null, TemplatesViewModel.ProblemsText(resolved.Problems));

        // A new task: the library starts one, where Retry and Run again are attempts at a task already begun.
        var (run, refusal) = runs.Again(user, known.RootPath, new Rerun(spec.Goal, spec, Guid.NewGuid()), "template run");
        return run is null ? (null, refusal ?? "Not started.") : (run.Id, "");
    }

    /// <summary>Saves a template given as JSON into the workspace's library or the global one; what to tell the person.</summary>
    public (bool Saved, string Said) SaveTemplate(ClaimsPrincipal user, string root, string json, TemplateScope scope)
    {
        if (Writing(user, root) is not { } known)
            return (false, Why(user, Role.Operator));
        if (TemplateStore.FromJson(json, out var unreadable) is not { } template)
            return (false, unreadable!);
        if (TemplateValidator.Validate(template) is { Count: > 0 } problems)
            return (false, "Not saved - " + string.Join("; ", problems));
        if (template.Builtin)
            return (false, "A built-in template is read-only; give it another id to keep your own copy.");

        new TemplateStore(known.RootPath).Save(template, scope);
        users.Audit(Who(user), "template saved", $"{template.Id} ({scope}) in {known.RootPath}");
        return (true, $"Saved '{template.Name}' to the {(scope == TemplateScope.Workspace ? "workspace's" : "global")} library.");
    }

    public string DeleteTemplate(ClaimsPrincipal user, string root, string id, TemplateScope scope)
    {
        if (Writing(user, root) is not { } known)
            return Why(user, Role.Operator);
        try
        {
            new TemplateStore(known.RootPath).Delete(id, scope);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
        users.Audit(Who(user), "template deleted", $"{id} ({scope}) in {known.RootPath}");
        return $"Deleted '{id}' from the {(scope == TemplateScope.Workspace ? "workspace's" : "global")} library.";
    }

    // ── schedules ───────────────────────────────────────────────────────────

    /// <summary>
    /// The schedules as the window shows them (SchedulesViewModel): the list, what each will do, what came of it, and
    /// the form - for a Viewer to read. Over the service's own schedules and settings, its tick's pulse (the warning that
    /// nothing runs is about the service's scheduler, not a console tick) and, for what is saved, switched and deleted,
    /// this service's checks: the form's changes come back here as the person (<see cref="SaveSchedule"/>,
    /// <see cref="SwitchSchedule"/>, <see cref="DeleteSchedule"/>), so a page can only offer what this allows.
    /// </summary>
    public (SchedulesViewModel? Schedules, string? Problem) Schedules(ClaimsPrincipal user, string root)
        => Reading(user, root) is { } known
            ? (new SchedulesViewModel(known.RootPath, () => engine.Settings ?? new AppSettings(), engine.Schedules,
                changes: new AsThePerson(this, user, known.RootPath), tickFile: engine.TickFile), null)
            : (null, Why(user, Role.Viewer));

    /// <summary>
    /// Keeps a schedule the form made, if the scheduler could run it: checked again here (ScheduleDrafts - the
    /// scheduler's own resolution), against this workspace and its templates as they are now, never trusted from the
    /// page's check - that was a view of a moment ago, and a template deleted since is a schedule that fails at its hour.
    /// Kept in the workspace this call names, whatever folder the schedule says. Every reason it was not, at once.
    /// </summary>
    public (bool Saved, string Said) SaveSchedule(ClaimsPrincipal user, string root, Schedule schedule)
    {
        if (Writing(user, root) is not { } known)
            return (false, Why(user, Role.Operator));

        // Its permissions are made again here: the tier it says it is at, with the settings' shell rule on top, asked of
        // the settings as they are now (SchedulePolicy). Taken as given, a looser policy than the settings allow would be
        // saved, and a schedule run commands on a service whose settings say never to.
        var permissions = new SchedulePolicy(() => engine.Settings ?? new AppSettings()).For(schedule.Permissions.Level);
        var draft = new ScheduleDraft(schedule.Name, known.RootPath, schedule.Work, schedule.Timing, permissions,
            schedule.Missed, schedule.Enabled, schedule.Id);
        var checkedDraft = ScheduleDrafts.Check(draft, WorkspaceInfo.For(known.RootPath), DateTimeOffset.Now, new TemplateStore(known.RootPath).Find);
        if (checkedDraft.Schedule is not { } kept)
            return (false, string.Join(Environment.NewLine, checkedDraft.Problems));

        var changed = engine.Schedules.For(known.RootPath).Any(s => s.Id == kept.Id);
        if (Written(() => engine.Schedules.Save(kept), () => engine.Schedules.For(known.RootPath).Any(s => s.Id == kept.Id && s.Name == kept.Name
                && s.Timing == kept.Timing && s.Enabled == kept.Enabled)) is { } failed)
            return (false, failed);
        users.Audit(Who(user), changed ? "schedule changed" : "schedule added", $"{kept.Name} in {known.RootPath}: {ScheduleWords.When(kept.Timing)}");
        return (true, $"Saved '{kept.Name}'. {ScheduleWords.NextRun(kept, DateTimeOffset.Now)}");
    }

    /// <summary>Turns a schedule of this workspace on or off; what to tell the person.</summary>
    public (bool Done, string Said) SwitchSchedule(ClaimsPrincipal user, string root, Guid id, bool on)
    {
        if (Writing(user, root) is not { } known)
            return (false, Why(user, Role.Operator));
        if (engine.Schedules.For(known.RootPath).FirstOrDefault(s => s.Id == id) is not { } schedule)
            return (false, "There is no such schedule here.");
        if (Written(() => engine.Schedules.Save(schedule with { Enabled = on }),
                () => engine.Schedules.For(known.RootPath).Any(s => s.Id == id && s.Enabled == on)) is { } failed)
            return (false, failed);
        users.Audit(Who(user), on ? "schedule switched on" : "schedule switched off", $"{schedule.Name} in {known.RootPath}");
        return (true, on ? $"'{schedule.Name}' is on." : $"'{schedule.Name}' is off.");
    }

    /// <summary>Forgets a schedule of this workspace; what to tell the person.</summary>
    public (bool Done, string Said) DeleteSchedule(ClaimsPrincipal user, string root, Guid id)
    {
        if (Writing(user, root) is not { } known)
            return (false, Why(user, Role.Operator));
        if (engine.Schedules.For(known.RootPath).FirstOrDefault(s => s.Id == id) is not { } schedule)
            return (false, "There is no such schedule here.");
        if (Written(() => engine.Schedules.Remove(known.RootPath, id), () => engine.Schedules.For(known.RootPath).All(s => s.Id != id)) is { } failed)
            return (false, failed);
        users.Audit(Who(user), "schedule deleted", $"{schedule.Name} in {known.RootPath}");
        return (true, $"Deleted '{schedule.Name}'.");
    }

    /// <summary>
    /// Makes a change to the schedules file and says whether it took: null when it did, otherwise what to tell the person.
    /// The store swallows a write that fails - a locked or read-only file - and throws only when another process holds
    /// its lock too long; the service said "Saved" for a change that was not on the disk, and the exception ended the
    /// person's page. So it is read back, and either failure is said in words.
    /// </summary>
    private static string? Written(Action change, Func<bool> took)
    {
        try
        {
            change();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return CouldNotWrite;
        }
        return took() ? null : CouldNotWrite;
    }

    private const string CouldNotWrite = "The service could not write its schedules file, so nothing was changed. Try again in a moment.";

    private WorkspaceEntry? Reading(ClaimsPrincipal user, string root)
        => ServerAuth.StillValid(users, user) is { Role: >= Role.Viewer } ? engine.Workspaces.Find(root) : null;

    private WorkspaceEntry? Writing(ClaimsPrincipal user, string root)
        => ServerAuth.StillValid(users, user) is { Role: >= Role.Operator } ? engine.Workspaces.Find(root) : null;

    private string Who(ClaimsPrincipal user) => user.Identity?.Name ?? "?";

    /// <summary>The policy a template is narrowed from: the folder's saved level, with the settings' shell rule on top.</summary>
    private PermissionPolicy PolicyOf(WorkspaceEntry known)
        => EngineComposition.PolicyFor(engine.Settings ?? new AppSettings(), known.Autonomy);

    /// <summary>
    /// The tools the service's engine registers, built from its settings as they are now (EngineComposition.Tools, as the
    /// window's Settings asks): a deny list typed against a hand-kept list restricted tools nobody had (TemplateEditViewModel).
    /// </summary>
    private IReadOnlyList<string> ToolNames()
        => EngineComposition.Tools(engine.Settings ?? new AppSettings()).Definitions.Select(d => d.Name).ToList();

    /// <summary>
    /// Where a draft may be saved, the one offered first. A template with a file of its own is offered its own
    /// library first: offered another, Save would write a second copy and leave the first where it was. A draft with no
    /// file yet - a new template, a duplicate, a built-in's copy - is offered the window's order, Global first
    /// (TemplateSettingsViewModel.Edit offers [Global, Workspace]), so the same choice reads the same in both.
    /// </summary>
    private static IReadOnlyList<TemplateScope> Scopes(TemplateScope? own)
        => own == TemplateScope.Workspace ? [TemplateScope.Workspace, TemplateScope.Global] : [TemplateScope.Global, TemplateScope.Workspace];

    private string Why(ClaimsPrincipal user, Role needed, string what = "changing templates and schedules")
        => ServerAuth.StillValid(users, user) is not { } account ? "Your session has ended. Sign in again."
            : account.Role < needed ? $"Your role does not include {what}."
            : "The service does not know that folder as a workspace.";

    /// <summary>
    /// The schedules form's changes, as the person whose page it is: each goes through this service's check for them
    /// (an Operator's) and its audit trail, and what it says when it refuses is what the form shows. On success the form
    /// says what the window's says - nothing, the list shows it.
    /// </summary>
    private sealed class AsThePerson(ServerLibrary library, ClaimsPrincipal user, string root) : IScheduleChanges
    {
        public string? Save(Schedule schedule)
            => library.SaveSchedule(user, root, schedule) is (false, var why) ? why : null;

        public string? Switch(Schedule schedule, bool on)
            => library.SwitchSchedule(user, root, schedule.Id, on) is (false, var why) ? why : null;

        public string? Remove(Schedule schedule)
            => library.DeleteSchedule(user, root, schedule.Id) is (false, var why) ? why : null;
    }
}
