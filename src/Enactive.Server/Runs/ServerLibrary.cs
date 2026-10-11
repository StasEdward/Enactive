namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Core.Schedules;
using Enactive.Core.Templates;
using Enactive.Server.Accounts;
using Enactive.Settings;
using Enactive.Workspace;

/// <summary>A schedule as the page lists it: when, what next, what it may do, and its last outcomes.</summary>
internal sealed record ScheduleView(Schedule Schedule, string When, string Next, IReadOnlyList<string> MayDo, IReadOnlyList<InboxItem> Outcomes);

/// <summary>What the schedule form filled in, as text - turned into a draft and checked here, not on the page.</summary>
internal sealed record ScheduleForm(
    Guid? Id, string Name, string Work, string Repeat, string Time, string? Day, string? Date, string Tier, bool RunLate,
    string Parameters);

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
internal sealed class ServerLibrary(UserStore users, ServerEngine engine)
{
    /// <summary>The templates seen from a workspace: its own, the service account's global ones, the built-ins.</summary>
    public (IReadOnlyList<StoredTemplate> Templates, string? Problem) Templates(ClaimsPrincipal user, string root)
        => Reading(user, root) is { } known ? (new TemplateStore(known.RootPath).Inventory(), null) : ([], Why(user, Role.Viewer));

    /// <summary>A template as its file holds it, for the editor; a new one's skeleton when <paramref name="id"/> is empty.</summary>
    public (string? Json, string? Problem) TemplateText(ClaimsPrincipal user, string root, string? id)
    {
        if (Reading(user, root) is not { } known)
            return (null, Why(user, Role.Viewer));
        if (string.IsNullOrWhiteSpace(id))
            return (TemplateStore.ToJson(new TaskTemplate("my-task", "My task", "Describe the task. {topic}",
                Parameters: [new TemplateParameter("topic", "Topic", TemplateParameterType.Text)])), null);
        return new TemplateStore(known.RootPath).Find(id) is { } template
            ? (TemplateStore.ToJson(template), null)
            : (null, $"There is no template '{id}' here.");
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

    public async Task<(IReadOnlyList<ScheduleView> Schedules, string? Problem)> SchedulesAsync(ClaimsPrincipal user, string root, CancellationToken ct)
    {
        if (Reading(user, root) is not { } known)
            return ([], Why(user, Role.Viewer));
        IReadOnlyList<InboxItem> inbox;
        try { inbox = await InboxStoreFactory.Create(WorkspaceInfo.For(known.RootPath)).LoadAllAsync(ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { inbox = []; }

        var now = DateTimeOffset.Now;
        return (engine.Schedules.For(known.RootPath)
            .Select(s => new ScheduleView(s, ScheduleWords.When(s.Timing), ScheduleWords.NextRun(s, now),
                ScheduleWords.MayDo(s.Permissions), ScheduledOutcome.Of(inbox, s.Id)))
            .ToList(), null);
    }

    /// <summary>Saves a schedule from the form, if the scheduler could run it; every reason it could not, at once.</summary>
    public (bool Saved, string Said) SaveSchedule(ClaimsPrincipal user, string root, ScheduleForm form)
    {
        if (Writing(user, root) is not { } known)
            return (false, Why(user, Role.Operator));
        if (Timing(form) is not { } timing)
            return (false, "The time is not a time of day (\"07:30\"), or a one-off has no date.");

        var work = form.Work.StartsWith("template:", StringComparison.Ordinal)
            ? ScheduledWork.FromTemplate(form.Work["template:".Length..], Parameters(form.Parameters))
            : form.Work.StartsWith("run:", StringComparison.Ordinal) && Guid.TryParse(form.Work["run:".Length..], out var runId)
                ? PastRun(known.RootPath, runId) is { } snapshot ? ScheduledWork.FromPastRun(snapshot) : null
                : null;
        if (work is null)
            return (false, "Choose what it runs: a template, or a past run that came from one.");

        // The tier with the settings' shell rule on top, from the settings as they are now (SchedulePolicy): a schedule
        // saved with the tier alone would run commands on a service whose settings say never to.
        var policy = new SchedulePolicy(() => engine.Settings ?? new AppSettings()).For(form.Tier);
        var draft = new ScheduleDraft(form.Name.Trim(), known.RootPath, work, timing, policy,
            form.RunLate ? MissedRun.RunLate : MissedRun.Skip, Enabled: true, Id: form.Id);
        var templates = new TemplateStore(known.RootPath);
        var checkedDraft = ScheduleDrafts.Check(draft, WorkspaceInfo.For(known.RootPath), DateTimeOffset.Now, templates.Find);
        if (checkedDraft.Schedule is not { } schedule)
            return (false, "Not saved - " + string.Join(" ", checkedDraft.Problems));

        engine.Schedules.Save(schedule);
        users.Audit(Who(user), form.Id is null ? "schedule added" : "schedule changed", $"{schedule.Name} in {known.RootPath}: {ScheduleWords.When(timing)}");
        return (true, $"Saved '{schedule.Name}'. {ScheduleWords.NextRun(schedule, DateTimeOffset.Now)}");
    }

    public string SwitchSchedule(ClaimsPrincipal user, string root, Guid id, bool on)
    {
        if (Writing(user, root) is not { } known)
            return Why(user, Role.Operator);
        if (engine.Schedules.For(known.RootPath).FirstOrDefault(s => s.Id == id) is not { } schedule)
            return "There is no such schedule here.";
        engine.Schedules.Save(schedule with { Enabled = on });
        users.Audit(Who(user), on ? "schedule switched on" : "schedule switched off", $"{schedule.Name} in {known.RootPath}");
        return on ? $"'{schedule.Name}' is on." : $"'{schedule.Name}' is off.";
    }

    public string DeleteSchedule(ClaimsPrincipal user, string root, Guid id)
    {
        if (Writing(user, root) is not { } known)
            return Why(user, Role.Operator);
        if (engine.Schedules.For(known.RootPath).FirstOrDefault(s => s.Id == id) is not { } schedule)
            return "There is no such schedule here.";
        engine.Schedules.Remove(known.RootPath, id);
        users.Audit(Who(user), "schedule deleted", $"{schedule.Name} in {known.RootPath}");
        return $"Deleted '{schedule.Name}'.";
    }

    /// <summary>The past runs a schedule can repeat: those from a template, whose task was recorded whole.</summary>
    public async Task<IReadOnlyList<Core.History.RunSummary>> RepeatableAsync(ClaimsPrincipal user, string root, CancellationToken ct)
    {
        if (Reading(user, root) is not { } known)
            return [];
        var summaries = await RunStoreFactory.Create(WorkspaceInfo.For(known.RootPath)).LoadSummariesAsync(ct);
        return summaries.Where(s => !string.IsNullOrWhiteSpace(s.Spec)).OrderByDescending(s => s.StartedAt).Take(20).ToList();
    }

    private string? PastRun(string root, Guid runId)
        => RunStoreFactory.Create(WorkspaceInfo.For(root)).LoadSummariesAsync(CancellationToken.None).GetAwaiter().GetResult()
            .FirstOrDefault(s => s.RunId == runId)?.Spec;

    /// <summary>The form's time, as a timing in this machine's zone - the service's clock is the one that fires it.</summary>
    private static ScheduleTiming? Timing(ScheduleForm form)
    {
        if (!TimeOnly.TryParse(form.Time, out var at))
            return null;
        return form.Repeat switch
        {
            "weekly" when Enum.TryParse<DayOfWeek>(form.Day, ignoreCase: true, out var day) => ScheduleTiming.Weekly(day, at),
            "once" when DateOnly.TryParse(form.Date, out var date) => ScheduleTiming.Once(new DateTimeOffset(date.ToDateTime(at))),
            "daily" => ScheduleTiming.Daily(at),
            _ => null
        };
    }

    /// <summary>"name=value" lines: the answers a template's parameters are given at the scheduled hour, when nobody is there to ask.</summary>
    private static Dictionary<string, string> Parameters(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0].Trim().Length > 0)
            .ToDictionary(pair => pair[0].Trim(), pair => pair[1].Trim());

    private WorkspaceEntry? Reading(ClaimsPrincipal user, string root)
        => ServerAuth.StillValid(users, user) is { Role: >= Role.Viewer } ? engine.Workspaces.Find(root) : null;

    private WorkspaceEntry? Writing(ClaimsPrincipal user, string root)
        => ServerAuth.StillValid(users, user) is { Role: >= Role.Operator } ? engine.Workspaces.Find(root) : null;

    private string Who(ClaimsPrincipal user) => user.Identity?.Name ?? "?";

    private string Why(ClaimsPrincipal user, Role needed)
        => ServerAuth.StillValid(users, user) is not { } account ? "Your session has ended. Sign in again."
            : account.Role < needed ? "Your role does not include changing templates and schedules."
            : "The service does not know that folder as a workspace.";
}
