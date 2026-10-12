namespace Enactive.Server.Tests;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// A workspace's templates and schedules, kept from the browser (server plan phase 3) - by the window's own rules:
/// TemplateValidator for a template, ScheduleDrafts (the scheduler's own resolution) for a schedule.
/// Only the workspace scope is written here: the global library is the real %APPDATA% of whoever runs the tests.
/// </summary>
public sealed class ServerLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-library", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly UserStore _users;
    private readonly ServerEngine _engine;
    private readonly ServerLibrary _library;
    private readonly RunControl _runs;

    private const string Password = "correct horse battery";

    private const string Digest = """
        { "id": "digest", "name": "Digest", "goal": "Write the digest of {topic}.",
          "parameters": [ { "id": "topic", "name": "Topic", "type": "Text", "required": true } ] }
        """;

    public ServerLibraryTests()
    {
        _workspace = Path.Combine(_root, "project");
        Directory.CreateDirectory(_workspace);
        _users = new UserStore(Path.Combine(_root, "data"));
        _users.Add("alice", Password, Role.Operator);
        _users.Add("vera", Password, Role.Viewer);
        _engine = new ServerEngine(Path.Combine(_root, "data"), new FileLogSink(Path.Combine(_root, "logs")));
        _engine.Workspaces.Touch(_workspace);
        // The runs' real door (RunControl checks the role and the folder), over an engine that ends a run at once.
        _runs = new RunControl(_users, new RunQueue(maxActive: 2), new EndsAtOnce(Path.Combine(_root, "permissions.json")),
            NullLogger<RunControl>.Instance);
        _library = new ServerLibrary(_users, _engine, _runs);
    }

    public void Dispose()
    {
        _engine.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private ClaimsPrincipal As(string name) => ServerAuth.PrincipalOf(_users.Find(name)!);

    private ScheduleForm Daily(string work, string parameters = "", string tier = "execute")
        => new(null, "morning digest", work, "daily", "07:30", null, null, tier, false, parameters);

    [Fact]
    public void A_template_given_as_json_is_kept_in_the_workspace_library_and_audited()
    {
        var (saved, said) = _library.SaveTemplate(As("alice"), _workspace, Digest, TemplateScope.Workspace);

        Assert.True(saved, said);
        Assert.Equal("Digest", new TemplateStore(_workspace).Find("digest")!.Name);
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "alice", Action: "template saved" });
    }

    [Theory]
    [InlineData("{ not json", "not a template as JSON")]
    [InlineData("""{ "id": "../escape", "name": "x", "goal": "x" }""", "Not saved")]
    public void A_template_that_is_not_one_is_not_written(string json, string said)
    {
        var (saved, why) = _library.SaveTemplate(As("alice"), _workspace, json, TemplateScope.Workspace);

        Assert.False(saved);
        Assert.Contains(said, why);
        Assert.False(Directory.Exists(Path.Combine(_workspace, ".enactive", "templates")) && Directory.EnumerateFiles(Path.Combine(_workspace, ".enactive", "templates")).Any());
    }

    [Fact]
    public void A_viewer_reads_the_library_and_changes_nothing()
    {
        Assert.Contains("role does not include", _library.SaveTemplate(As("vera"), _workspace, Digest, TemplateScope.Workspace).Said);
        Assert.NotEmpty(_library.Templates(As("vera"), _workspace).Templates);   // the built-ins at least
    }

    /// <summary>
    /// Checked as the scheduler will check it at its hour: a template parameter nobody supplies is refused now, in
    /// words, not at three in the morning.
    /// </summary>
    [Fact]
    public void A_schedule_the_scheduler_could_not_run_is_refused_with_why()
    {
        _library.SaveTemplate(As("alice"), _workspace, Digest, TemplateScope.Workspace);

        var (saved, said) = _library.SaveSchedule(As("alice"), _workspace, Daily("template:digest"));

        Assert.False(saved);
        Assert.Contains("topic", said);
        Assert.Empty(_engine.Schedules.For(_workspace));
    }

    [Fact]
    public void A_schedule_with_its_parameters_is_saved_switched_and_deleted()
    {
        _library.SaveTemplate(As("alice"), _workspace, Digest, TemplateScope.Workspace);

        var (saved, said) = _library.SaveSchedule(As("alice"), _workspace, Daily("template:digest", "topic=the inbox"));

        Assert.True(saved, said);
        var schedule = Assert.Single(_engine.Schedules.For(_workspace));
        Assert.Equal("the inbox", schedule.Work.Parameters["topic"]);
        Assert.Equal(new TimeOnly(7, 30), schedule.Timing.AtLocal);

        _library.SwitchSchedule(As("alice"), _workspace, schedule.Id, on: false);
        Assert.False(_engine.Schedules.For(_workspace).Single().Enabled);
        _library.DeleteSchedule(As("alice"), _workspace, schedule.Id);
        Assert.Empty(_engine.Schedules.For(_workspace));
    }

    /// <summary>The tier with the settings' shell rule on top, from the settings as they are when it is saved.</summary>
    [Fact]
    public void A_schedule_s_permissions_follow_the_settings_shell_rule()
    {
        var settings = Enactive.Settings.AppSettings.Load(_engine.SettingsFile);
        settings.ShellCommands = Enactive.Settings.ShellCommandPolicy.Off;
        settings.Save(_engine.SettingsFile, replaceUnreadable: true);
        _engine.Reload();
        _library.SaveTemplate(As("alice"), _workspace, Digest, TemplateScope.Workspace);

        _library.SaveSchedule(As("alice"), _workspace, Daily("template:digest", "topic=the inbox", tier: "autonomous"));

        var expected = Enactive.Settings.EngineComposition.PolicyFor(settings, 3);
        Assert.Equal(expected.AskBefore, Assert.Single(_engine.Schedules.For(_workspace)).Permissions.AskBefore);
        Assert.Equal(expected.Allow, _engine.Schedules.For(_workspace).Single().Permissions.Allow);
    }

    [Fact]
    public void A_time_that_is_not_one_is_refused()
        => Assert.Contains("not a time", _library.SaveSchedule(As("alice"), _workspace,
            new ScheduleForm(null, "x", "template:digest", "daily", "25:99", null, null, "execute", false, "")).Said);

    // ── the library page and the editor (Task 16) ──────────────────────────

    /// <summary>
    /// Run from the library as the window runs one (TemplatesWindow → MainWindow.RunAsync): the template resolved with the
    /// answers filled in, against the folder's level as it is now, and started as that saved task - its goal is the run's.
    /// </summary>
    [Fact]
    public async Task A_template_is_run_with_its_answers_as_the_saved_task_it_is()
    {
        _library.SaveTemplate(As("alice"), _workspace, Digest, TemplateScope.Workspace);

        var (id, said) = _library.RunTemplate(As("alice"), _workspace, "digest", new Dictionary<string, string> { ["topic"] = "the inbox" });

        Assert.True(id is not null, said);
        var run = _runs.Find(As("alice"), id!.Value)!;
        Assert.Equal("Write the digest of the inbox.", run.Prompt);
        Assert.Equal(("digest", "the inbox"), (run.Spec!.TemplateId, run.Spec.Parameters["topic"]));
        Assert.Equal(Enactive.Settings.EngineComposition.PolicyFor(_engine.Settings ?? new Enactive.Settings.AppSettings(), 2).Level,
            run.Spec.Permissions.Level);
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "alice", Action: "template run" });
        await run.Ended;
    }

    [Fact]
    public void A_template_missing_an_answer_is_not_run_and_says_which()
    {
        _library.SaveTemplate(As("alice"), _workspace, Digest, TemplateScope.Workspace);

        var (id, said) = _library.RunTemplate(As("alice"), _workspace, "digest", new Dictionary<string, string>());

        Assert.Null(id);
        // In the library's Problems words (TemplatesViewModel.ProblemsText): a line per problem.
        Assert.StartsWith("• ", said);
        Assert.Contains("Topic", said);
        Assert.Empty(_runs.List(As("alice")));
    }

    [Fact]
    public void A_viewer_runs_no_template_and_opens_none_to_edit()
    {
        _library.SaveTemplate(As("alice"), _workspace, Digest, TemplateScope.Workspace);

        var (id, said) = _library.RunTemplate(As("vera"), _workspace, "digest", new Dictionary<string, string> { ["topic"] = "x" });
        var (draft, problem) = _library.Draft(As("vera"), _workspace, "digest", copy: false);

        Assert.Null(id);
        Assert.Contains("role does not include", said);
        Assert.Null(draft);
        Assert.Contains("role does not include", problem);
        Assert.Empty(_runs.List(As("alice")));
    }

    /// <summary>The library is the window's (TemplatesViewModel), pointed at the workspace and at its level as the service has it.</summary>
    [Fact]
    public void The_library_is_the_window_s_for_the_workspace_and_a_viewer_reads_it()
    {
        _library.SaveTemplate(As("alice"), _workspace, Digest, TemplateScope.Workspace);

        var (library, problem) = _library.Library(As("vera"), _workspace);

        Assert.Null(problem);
        Assert.Equal(_workspace, library!.WorkspaceRoot);
        library.Selected = library.Templates.Single(t => t.Template.Id == "digest");
        Assert.StartsWith("runs at Execute", library.SelectedSummary);
        Assert.Contains("this workspace", library.Selected.Meta);
        Assert.Contains("The service does not know", _library.Library(As("vera"), Path.Combine(_root, "elsewhere")).Problem);
    }

    /// <summary>
    /// What the editor opens: a template's own file in its own library first, a built-in as a copy of the same id, a
    /// duplicate under an id nothing uses, a new one blank - the window's rules (TemplateDrafts) - with the tools this
    /// service's engine registers for the deny and ask lists. A draft with no file of its own yet - new, a duplicate, a
    /// built-in's copy - is offered the libraries in the window's order, Global first (Ruling 16).
    /// </summary>
    [Fact]
    public void The_editor_opens_what_the_window_s_would()
    {
        _library.SaveTemplate(As("alice"), _workspace, Digest, TemplateScope.Workspace);

        var own = _library.Draft(As("alice"), _workspace, "digest", copy: false).Draft!;
        Assert.Equal(("digest", false, TemplateScope.Workspace), (own.Template.Id, own.IdEditable, own.Scopes[0]));
        Assert.Contains("read_file", own.ToolNames);

        var copy = _library.Draft(As("alice"), _workspace, "digest", copy: true).Draft!;
        Assert.Equal(("digest-copy", true), (copy.Template.Id, copy.IdEditable));

        var blank = _library.Draft(As("alice"), _workspace, null, copy: false).Draft!;
        Assert.Equal(("", true), (blank.Template.Id, blank.IdEditable));

        Assert.Contains("There is no template 'nothing' here.", _library.Draft(As("alice"), _workspace, "nothing", copy: false).Problem);
    }

    // A draft with no file of its own yet is offered the libraries in the window's order, Global first
    // (TemplateSettingsViewModel.Edit; Ruling 16): one each for a new template, a duplicate and a built-in's copy.

    [Fact]
    public void A_new_template_is_offered_the_global_library_first_as_the_window_offers_it()
        => Assert.Equal([TemplateScope.Global, TemplateScope.Workspace], _library.Draft(As("alice"), _workspace, null, copy: false).Draft!.Scopes);

    [Fact]
    public void A_duplicate_is_offered_the_global_library_first_as_the_window_offers_it()
    {
        _library.SaveTemplate(As("alice"), _workspace, Digest, TemplateScope.Workspace);

        Assert.Equal([TemplateScope.Global, TemplateScope.Workspace], _library.Draft(As("alice"), _workspace, "digest", copy: true).Draft!.Scopes);
    }

    [Fact]
    public void A_built_in_s_copy_is_offered_the_global_library_first_as_the_window_offers_it()
    {
        // A built-in no file shadows here (the runner's own global library may shadow some): opened as a copy of its id.
        var builtin = new TemplateStore(_workspace).Inventory().First(t => t.IsBuiltin).Template.Id;

        var customised = _library.Draft(As("alice"), _workspace, builtin, copy: false).Draft!;

        Assert.Equal((builtin, false, true), (customised.Template.Id, customised.Template.Builtin, customised.IdEditable));
        Assert.Equal([TemplateScope.Global, TemplateScope.Workspace], customised.Scopes);
    }

    /// <summary>The engine, stood in for: a run begins and ends with no step.</summary>
    private sealed class EndsAtOnce(string approvals) : IRunExecutor
    {
        public string? Problem => null;
        public Enactive.Core.Permissions.SessionApprovals Session { get; } = new();
        public Enactive.Core.Permissions.ApprovalStore Approvals { get; } = new(approvals);

        public Task ExecuteAsync(ServerRun run, Enactive.Core.Permissions.IDecisionHandler decisions, CancellationToken ct)
            => run.Session.DrainAsync(System.Linq.AsyncEnumerable.Empty<Enactive.Core.Events.WorkEvent>(), ct);
    }
}
