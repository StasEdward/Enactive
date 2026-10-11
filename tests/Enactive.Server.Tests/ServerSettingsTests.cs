namespace Enactive.Server.Tests;

using System.Security.Claims;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Enactive.Settings;

/// <summary>
/// The service's settings, edited by an Admin from the browser (server plan phase 3): validated before written, an
/// older copy never overwriting a newer, secrets on disk only as DPAPI, and the engine built again from what was saved.
/// </summary>
public sealed class ServerSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-settings", Guid.NewGuid().ToString("N"));
    private readonly UserStore _users;
    private readonly ServerEngine _engine;
    private readonly ServerSettings _settings;

    private const string Password = "correct horse battery";

    public ServerSettingsTests()
    {
        _users = new UserStore(_root);
        _users.Add("ada", Password, Role.Admin);
        _users.Add("alice", Password, Role.Operator);
        _engine = new ServerEngine(_root, new Enactive.Workspace.FileLogSink(Path.Combine(_root, "logs")));
        _settings = new ServerSettings(_users, _engine);
    }

    public void Dispose()
    {
        _engine.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private ClaimsPrincipal As(string name) => ServerAuth.PrincipalOf(_users.Find(name)!);

    private OpenedSettings Opened() => _settings.Open(As("ada")).Opened!;

    [Fact]
    public void An_admin_saves_and_the_file_holds_it()
    {
        var opened = Opened();
        opened.Copy.KeepRuns = 25;

        var (saved, said) = _settings.Save(As("ada"), opened);

        Assert.True(saved, said);
        Assert.Equal(25, AppSettings.Load(_engine.SettingsFile).KeepRuns);
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "ada", Action: "settings saved" });
    }

    /// <summary>Two pages open, or a page and the service account's desktop: the second save must not erase the first.</summary>
    [Fact]
    public void A_copy_opened_before_another_save_is_not_saved_over_it()
    {
        var first = Opened();
        var second = Opened();
        first.Copy.KeepRuns = 10;
        Assert.True(_settings.Save(As("ada"), first).Saved);

        second.Copy.KeepRuns = 99;
        var (saved, said) = _settings.Save(As("ada"), second);

        Assert.False(saved);
        Assert.Contains("changed since you opened them", said);
        Assert.Equal(10, AppSettings.Load(_engine.SettingsFile).KeepRuns);
    }

    /// <summary>A key typed into the page is on disk only as DPAPI for the service's account, and back in memory as it was.</summary>
    [Fact]
    public void A_key_is_written_protected_and_read_back_whole()
    {
        var opened = Opened();
        opened.Copy.Providers.Add(new ProviderConfig { Id = "cloud", Kind = Core.Providers.ProviderKind.Anthropic, BaseUrl = "https://api.example.com", ApiKey = "sk-very-secret", Models = ["big"] });

        Assert.True(_settings.Save(As("ada"), opened).Saved);

        Assert.DoesNotContain("sk-very-secret", File.ReadAllText(_engine.SettingsFile));
        Assert.Equal("sk-very-secret", Opened().Copy.Providers.Single(p => p.Id == "cloud").ApiKey);
    }

    /// <summary>A configuration that cannot be built, written anyway, would take the service down at its next start.</summary>
    [Fact]
    public void Settings_that_cannot_be_built_are_not_written()
    {
        var opened = Opened();
        opened.Copy.Providers.Add(new ProviderConfig { Id = "twice", BaseUrl = "http://a" });
        opened.Copy.Providers.Add(new ProviderConfig { Id = "twice", BaseUrl = "http://b" });

        var (saved, said) = _settings.Save(As("ada"), opened);

        Assert.False(saved);
        Assert.StartsWith("Not saved", said);
    }

    [Fact]
    public void Nobody_but_an_admin_opens_or_saves_them()
    {
        Assert.Contains("role does not include", _settings.Open(As("alice")).Problem);
        var opened = Opened();
        opened.Copy.KeepRuns = 3;

        Assert.False(_settings.Save(As("alice"), opened).Saved);
        Assert.NotEqual(3, AppSettings.Load(_engine.SettingsFile).KeepRuns);
    }

    /// <summary>Saved, the engine is built again from them: the next run is composed on what was saved.</summary>
    [Fact]
    public void A_saved_provider_and_its_bindings_give_the_service_an_engine()
    {
        Assert.NotNull(_engine.Problem);
        var opened = Opened();
        var s = opened.Copy;
        s.Providers.Clear();
        s.Providers.Add(new ProviderConfig { Id = "box", BaseUrl = "http://127.0.0.1:8080/v1", Models = ["qwen"] });
        s.Bindings.Plan = s.Bindings.Review = s.Bindings.ExecuteLight = s.Bindings.ExecuteHeavy = "box/qwen";
        foreach (var worker in s.Workers)
            worker.Model = "box/qwen";

        var (saved, said) = _settings.Save(As("ada"), opened);

        Assert.True(saved, said);
        Assert.Null(_engine.Problem);
        Assert.NotNull(_engine.Current);
    }
}
