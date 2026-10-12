namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Core.Schedules;
using Enactive.Server.Accounts;
using Enactive.Server.Components;
using Enactive.Server.Components.Pages;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Schedules as the window's (SchedulesWindow.axaml): the list, 320 px, with New and Refresh; for the chosen one "What this
/// will do" and "What came of it", with Delete, Enable / Disable and Edit; the form - Name, What it runs, How often, Day,
/// Date, Time, Time zone, What it may do. The page binds the window's own view model (SchedulesViewModel); every change is
/// ServerLibrary's to decide: a Viewer reads, an Operator saves, switches and deletes. The schedules are the service's own
/// (its data folder, the harness's temp root), never the runner's %APPDATA%.
/// </summary>
public sealed class SchedulesTests : ComponentHost
{
    private const string Weekly = """{ "id": "weekly", "name": "Weekly", "goal": "Write the week's summary." }""";

    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-schedules", Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public SchedulesTests()
    {
        Services.AddSingleton(new WorkspaceRoots([_folders]));
        _root = Path.Combine(_folders, "project");
        Directory.CreateDirectory(_root);
        Services.GetRequiredService<ServerEngine>().Workspaces.Touch(_root);
        var (saved, said) = Library.SaveTemplate(As("alice"), _root, Weekly, TemplateScope.Workspace);
        Assert.True(saved, said);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Enactive.Tests.SqlitePools.ClearUnder(_folders);
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
        }
    }

    private ServerLibrary Library => Services.GetRequiredService<ServerLibrary>();

    private ServerEngine Engine => Services.GetRequiredService<ServerEngine>();

    private Guid WorkspaceId => WorkspaceInfo.For(_root).Id;

    private System.Security.Claims.ClaimsPrincipal As(string who) => ServerAuth.PrincipalOf(Users.Find(who)!);

    /// <summary>A schedule saved as an Operator's form saves one: every day at 07:30, at the Execute tier.</summary>
    private Schedule Saved(string name = "morning summary")
    {
        var schedule = new Schedule(Guid.NewGuid(), _root, name, ScheduledWork.FromTemplate("weekly"),
            ScheduleTiming.Daily(new TimeOnly(7, 30)), new SchedulePolicy(() => Engine.Settings ?? new Enactive.Settings.AppSettings()).For("execute"),
            DateTimeOffset.Now);
        var (saved, said) = Library.SaveSchedule(As("alice"), _root, schedule);
        Assert.True(saved, said);
        return Engine.Schedules.For(_root).Single(s => s.Name == name);
    }

    private IRenderedComponent<SchedulesScreen> Screen(string who, Guid? schedule = null)
    {
        SignIn(who);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/" + Links.Schedules(WorkspaceId, schedule));
        var screen = Render<SchedulesScreen>(p => p.Add(s => s.ScheduleId, schedule));
        screen.WaitForAssertion(() => Assert.NotEmpty(screen.FindAll(".schedules-status")));
        return screen;
    }

    private static string[] Offered(IRenderedComponent<SchedulesScreen> screen)
        => screen.FindAll("button, a").Select(b => (b.GetAttribute("aria-label") ?? b.TextContent).Trim()).ToArray();

    private static void Press(IRenderedComponent<SchedulesScreen> screen, string button)
        => screen.FindAll("button").Single(b => b.TextContent.Trim() == button).Click();

    /// <summary>
    /// A schedule is an approval given in advance, so what it will agree to is read before it is agreed: as the form is
    /// filled in, "What this will do" says the window's lines of the schedule ScheduleDrafts makes of it - when it runs
    /// next among them - and nothing is saved until Save. Saved, it is listed with its next run, and chosen.
    /// </summary>
    [Fact]
    public void A_new_schedule_says_when_it_runs_next_before_it_is_saved()
    {
        var screen = Screen("alice");

        Press(screen, "New");
        screen.Find("#s-name").Input("morning summary");
        var weekly = screen.FindAll("#s-work option").Single(o => o.TextContent.Trim() == "Template · Weekly");
        screen.Find("#s-work").Change(weekly.GetAttribute("value"));
        screen.Find("input[aria-label='Hour']").Change("7");
        screen.Find("input[aria-label='Minute']").Change("30");

        var draft = screen.Find(".schedule-draft-approval").TextContent;
        Assert.Contains("Next run: ", draft);
        Assert.Contains("Every day at 07:30", draft);
        Assert.Empty(Engine.Schedules.For(_root));

        Press(screen, "Save");

        screen.WaitForAssertion(() => Assert.Single(screen.FindAll(".schedule-card")));
        var saved = Assert.Single(Engine.Schedules.For(_root));
        Assert.Equal(("morning summary", "weekly", new TimeOnly(7, 30)), (saved.Name, saved.Work.TemplateId, saved.Timing.AtLocal));
        var card = screen.Find(".schedule-card");
        Assert.Contains("Next run: ", card.TextContent);
        Assert.Equal("true", card.GetAttribute("aria-current"));
        Assert.Contains(Users.AuditTrail(), e => e is { Account: "alice", Action: "schedule added" });
    }

    /// <summary>The time is two digits a side, as the window's spinners show it (FormatString="00").</summary>
    [Fact]
    public void The_time_is_shown_in_two_digits_as_the_window_s()
    {
        Saved();
        var screen = Screen("alice");

        Press(screen, "Edit");
        screen.Find("input[aria-label='Minute']").Change("5");

        Assert.Equal("07", screen.Find("input[aria-label='Hour']").GetAttribute("value"));
        Assert.Equal("05", screen.Find("input[aria-label='Minute']").GetAttribute("value"));
    }

    /// <summary>
    /// The form says why it cannot be saved, every reason at once, in the scheduler's words - and keeps what was typed.
    /// </summary>
    [Fact]
    public void A_schedule_the_scheduler_could_not_run_is_not_saved_and_says_why()
    {
        var screen = Screen("alice");

        Press(screen, "New");
        Press(screen, "Save");

        var problems = screen.Find(".schedule-problems");
        Assert.Equal("alert", problems.GetAttribute("role"));
        Assert.Contains("Give it a name", problems.TextContent);
        Assert.Empty(Engine.Schedules.For(_root));
        Assert.NotEmpty(screen.FindAll("#s-name"));
    }

    /// <summary>
    /// What came of a schedule is the inbox's items it produced, matched by its id - newest first, each drawn as the inbox
    /// draws its kind - and never another schedule's.
    /// </summary>
    [Fact]
    public async Task What_came_of_it_lists_the_schedule_s_outcomes()
    {
        var mine = Saved("morning summary");
        var other = Saved("evening summary");
        var inbox = InboxStoreFactory.Create(WorkspaceInfo.For(_root));
        await inbox.AppendAsync(ScheduledOutcome.CouldNotStart(WorkspaceId, mine.Id, mine.Name, "the folder was busy", DateTimeOffset.Now.AddHours(-2)), CancellationToken.None);
        await inbox.AppendAsync(new InboxItem(Guid.NewGuid(), WorkspaceId, "result", mine.Name, "Wrote the summary.",
            Guid.NewGuid(), "unread", DateTimeOffset.Now.AddHours(-1), mine.Id), CancellationToken.None);
        await inbox.AppendAsync(ScheduledOutcome.CouldNotStart(WorkspaceId, other.Id, other.Name, "not mine", DateTimeOffset.Now), CancellationToken.None);

        var screen = Screen("vera", mine.Id);

        screen.WaitForAssertion(() => Assert.Equal(2, screen.FindAll(".schedule-outcome").Count));
        var outcomes = screen.FindAll(".schedule-outcome");
        Assert.Equal(["Wrote the summary.", "Did not run · the folder was busy"],
            outcomes.Select(o => o.QuerySelector(".schedule-outcome-summary")!.TextContent.Trim()));
        Assert.Contains(Looks.Inbox("result"), outcomes[0].ClassList);
        Assert.Contains(Looks.Inbox("error"), outcomes[1].ClassList);
        Assert.DoesNotContain("not mine", screen.Find(".schedule-detail").TextContent);
        Assert.DoesNotContain("Nothing yet", screen.Find(".schedule-detail").TextContent);
    }

    [Fact]
    public void A_schedule_that_has_not_reported_back_says_so()
    {
        Saved();

        var screen = Screen("vera");

        Assert.Contains("Nothing yet — this schedule has not reported back.", screen.Find(".schedule-detail").TextContent);
    }

    /// <summary>
    /// A Viewer reads the list, what each will do and what came of it, and is offered nothing that changes one: no New,
    /// Edit, Delete or Disable. Refresh stays - it changes nothing.
    /// </summary>
    [Fact]
    public void A_viewer_is_offered_no_new_edit_or_delete()
    {
        Saved();

        var screen = Screen("vera");

        Assert.Equal("morning summary", screen.Find(".schedule-card .card-title").TextContent.Trim());
        Assert.Contains("Every day at 07:30", screen.Find(".schedule-approval").TextContent);
        var offered = Offered(screen);
        Assert.Contains("Refresh", offered);
        Assert.DoesNotContain("New", offered);
        Assert.DoesNotContain("Edit", offered);
        Assert.DoesNotContain("Delete", offered);
        Assert.DoesNotContain("Disable", offered);
        Assert.DoesNotContain("Save", offered);
        Assert.Empty(screen.FindAll(".schedule-form"));
    }

    /// <summary>The window's buttons on a saved one, through the service: Disable (then Enable), Delete - each audited.</summary>
    [Fact]
    public void An_operator_switches_one_off_and_deletes_it()
    {
        Saved();
        var screen = Screen("alice");

        Press(screen, "Disable");

        screen.WaitForAssertion(() => Assert.Contains("Enable", Offered(screen)));
        Assert.False(Engine.Schedules.For(_root).Single().Enabled);
        Assert.Contains(Looks.Schedule(ScheduleState.Off), screen.Find(".schedule-card").ClassList);

        Press(screen, "Delete");

        screen.WaitForAssertion(() => Assert.Empty(screen.FindAll(".schedule-card")));
        Assert.Empty(Engine.Schedules.For(_root));
        Assert.Contains("No schedules in this workspace yet.", screen.Find(".schedules-status").TextContent);
        Assert.Contains(Users.AuditTrail(), e => e is { Account: "alice", Action: "schedule deleted" });
    }

    /// <summary>
    /// Edit opens the form on the saved schedule, and Save keeps it under its own id - its outcomes are filed against that
    /// id, and a new one would have started its history again.
    /// </summary>
    [Fact]
    public void Edit_keeps_the_schedule_s_id()
    {
        var saved = Saved();
        var screen = Screen("alice");

        Press(screen, "Edit");
        Assert.Equal("morning summary", screen.Find("#s-name").GetAttribute("value"));
        screen.Find("#s-name").Input("summary at dawn");
        Press(screen, "Save");

        screen.WaitForAssertion(() => Assert.Equal("summary at dawn", screen.Find(".schedule-card .card-title").TextContent.Trim()));
        Assert.Equal(saved.Id, Assert.Single(Engine.Schedules.For(_root)).Id);
    }

    /// <summary>Each schedule's edge says what its second line says, for the glance - as Palette.Schedule draws it.</summary>
    [Fact]
    public void Each_schedule_s_edge_says_whether_it_will_run()
    {
        Saved("on");
        var off = Saved("off");
        Library.SwitchSchedule(As("alice"), _root, off.Id, on: false);

        var screen = Screen("vera");

        var cards = screen.FindAll(".schedule-card").ToDictionary(c => c.QuerySelector(".card-title")!.TextContent.Trim());
        Assert.Contains(Looks.Schedule(ScheduleState.Scheduled), cards["on"].ClassList);
        Assert.Contains(Looks.Schedule(ScheduleState.Off), cards["off"].ClassList);
    }

    /// <summary>An address that names a schedule opens on it - an old address does, and a link to one.</summary>
    [Fact]
    public void An_address_that_names_a_schedule_opens_on_it()
    {
        Saved("first");
        var second = Saved("second");

        var screen = Screen("vera", second.Id);

        Assert.Equal("second", screen.Find(".schedule-card[aria-current='true'] .card-title").TextContent.Trim());
    }

    /// <summary>
    /// The warning that none of them will run is about the service's own scheduler: said until it has asked them, gone once
    /// it has - not about a console tick the service does not need.
    /// </summary>
    [Fact]
    public async Task The_page_says_nothing_will_run_until_the_service_s_scheduler_has_asked()
    {
        Assert.Contains("none of them will run", Screen("vera").Find(".schedules-tick").TextContent);

        await new ServerScheduler(Engine, Services.GetRequiredService<IRunControl>(), NullLogger<ServerScheduler>.Instance)
            .TickAsync(DateTimeOffset.Now, CancellationToken.None);

        Assert.Empty(Screen("vera").FindAll(".schedules-tick"));
    }

    [Fact]
    public void A_workspace_the_service_does_not_know_says_so()
    {
        SignIn("vera");
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/schedules?ws={Guid.NewGuid()}");

        var screen = Render<SchedulesScreen>();

        Assert.Contains("does not know that workspace", screen.Markup);
        Assert.Empty(screen.FindAll(".schedules-body"));
    }
}
