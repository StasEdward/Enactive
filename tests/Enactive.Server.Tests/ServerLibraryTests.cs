namespace Enactive.Server.Tests;

using System.Security.Claims;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Enactive.Workspace;

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
        _library = new ServerLibrary(_users, _engine);
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
}
