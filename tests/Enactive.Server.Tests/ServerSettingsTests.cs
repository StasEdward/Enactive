namespace Enactive.Server.Tests;

using System.Security.Claims;
using Enactive.Application;
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
        _settings = new ServerSettings(_users, _engine, new WorkspaceRoots([]));
    }

    public void Dispose()
    {
        _engine.Dispose();
        Enactive.Tests.SqlitePools.ClearUnder(_root);
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

    /// <summary>
    /// A key typed into the page is on disk only as DPAPI for the service's account, read back whole by the service - and
    /// not handed to the next page that opens the settings, neither as itself nor as its ciphertext.
    /// </summary>
    [Fact]
    public void A_key_is_written_protected_and_never_handed_back_to_a_page()
    {
        var opened = Opened();
        opened.Copy.Providers.Add(new ProviderConfig { Id = "cloud", Kind = Core.Providers.ProviderKind.Anthropic, BaseUrl = "https://api.example.com", ApiKey = "sk-very-secret", Models = ["big"] });

        Assert.True(_settings.Save(As("ada"), opened).Saved);

        Assert.DoesNotContain("sk-very-secret", File.ReadAllText(_engine.SettingsFile));
        var onDisk = AppSettings.Load(_engine.SettingsFile).Providers.Single(p => p.Id == "cloud");
        Assert.Equal("sk-very-secret", onDisk.ApiKey);
        var reopened = Opened().Copy.Providers.Single(p => p.Id == "cloud");
        Assert.Equal("", reopened.ApiKey);
        Assert.NotEqual(onDisk.ApiKeyProtected, reopened.ApiKeyProtected);
        Assert.True(AppSettings.IsKept(reopened.ApiKeyProtected));
    }

    /// <summary>
    /// Opened, something else changed, saved: the key and the mail password nobody on the page could see are still
    /// there - the copy it was handed had neither, and the save puts them back from the file.
    /// </summary>
    [Fact]
    public void A_save_keeps_every_secret_the_page_left_alone()
    {
        var stored = AppSettings.Load(_engine.SettingsFile);
        stored.Providers.Add(new ProviderConfig { Id = "cloud", BaseUrl = "https://api.example.com", ApiKey = "sk-very-secret" });
        stored.Smtp.Password = "mail-very-secret";
        Assert.True(stored.Save(_engine.SettingsFile, replaceUnreadable: true));

        var opened = Opened();
        opened.Copy.KeepRuns = 12;
        var (saved, said) = _settings.Save(As("ada"), opened);

        Assert.True(saved, said);
        var now = AppSettings.Load(_engine.SettingsFile);
        Assert.Equal((12, "sk-very-secret", "mail-very-secret"), (now.KeepRuns, now.Providers.Single(p => p.Id == "cloud").ApiKey, now.Smtp.Password));
        // What the page sent is still without them, should it be sent again.
        Assert.Equal("", opened.Copy.Smtp.Password);
    }

    /// <summary>
    /// The page's view model saves through the service as the person: written, audited, and what the service said handed
    /// back; a save the service refuses is said by the view model the way the window says it, "Not saved" once.
    /// </summary>
    [Fact]
    public async Task The_page_s_settings_save_through_the_service_as_the_person()
    {
        var said = new List<string>();
        var (view, problem) = _settings.Edit(As("ada"), null, said.Add);
        Assert.Null(problem);
        view!.LogRetentionDaysText = "3";

        await view.SaveCommand.ExecuteAsync();

        Assert.Equal(3, AppSettings.Load(_engine.SettingsFile).LogRetentionDays);
        Assert.StartsWith("Saved.", Assert.Single(said));
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "ada", Action: "settings saved" });

        // The file has changed since this one was opened.
        view.LogRetentionDaysText = "4";
        await view.SaveCommand.ExecuteAsync();
        Assert.StartsWith("Not saved — The settings were changed since you opened them", view.StartupNote);
        Assert.Equal(3, AppSettings.Load(_engine.SettingsFile).LogRetentionDays);
    }

    /// <summary>A role taken away while the page is open: its Save is refused by the service, and nothing is written.</summary>
    [Fact]
    public async Task A_page_whose_admin_was_demoted_saves_nothing()
    {
        var (view, _) = _settings.Edit(As("ada"), null, _ => { });
        _users.SetRole("ada", Role.Operator);
        view!.LogRetentionDaysText = "5";

        await view.SaveCommand.ExecuteAsync();

        // A principal whose role is no longer the account's is a session that has ended (ServerAuth.StillValid).
        Assert.Equal("Not saved — Your session has ended. Sign in again.", view.StartupNote);
        Assert.NotEqual(5, AppSettings.Load(_engine.SettingsFile).LogRetentionDays);
    }

    /// <summary>
    /// A provider is asked as the service with the key it keeps, where the page's box was left empty: asked with no key,
    /// as the page holds none, a provider that is fine was drawn red. And nobody but an Admin asks.
    /// </summary>
    [Fact]
    public async Task A_provider_is_asked_with_the_key_kept_for_it()
    {
        using var provider = new OneAnswerProvider();
        var stored = AppSettings.Load(_engine.SettingsFile);
        stored.Providers.Add(new ProviderConfig { Id = "box", BaseUrl = provider.BaseUrl, ApiKey = "sk-kept-key", Models = ["qwen"] });
        Assert.True(stored.Save(_engine.SettingsFile, replaceUnreadable: true));

        var (view, _) = _settings.Edit(As("ada"), null, _ => { });
        var row = view!.Providers.Single(p => p.Name == "box");
        var editor = new ProviderEditViewModel(row.Config, () => { }, view.ProviderCalls);
        await editor.FetchCommand.ExecuteAsync();

        Assert.Equal("Bearer sk-kept-key", provider.Authorization);
        Assert.Contains("qwen", editor.FetchedModels);
        // The kept key went where the page pointed it: said in the audit trail, with where and by whom - never the key.
        var entry = Assert.Single(_users.AuditTrail(), e => e.Action == "provider checked");
        Assert.Equal(("ada", $"box: {provider.BaseUrl} - its models listed (with the credentials kept for it)"), (entry.Account, entry.Detail));

        var (operatorView, refused) = _settings.Edit(As("alice"), null, _ => { });
        Assert.Null(operatorView);
        Assert.Contains("role does not include", refused);
    }

    /// <summary>
    /// An address may carry a credential of its own - user info, a key or a token in the query: the audit trail says
    /// where a kept key went by scheme, host, port and path only, as written whole it put that credential in the trail.
    /// </summary>
    [Fact]
    public async Task A_kept_key_sent_to_an_address_with_its_own_credentials_is_audited_without_them()
    {
        var stored = AppSettings.Load(_engine.SettingsFile);
        stored.Providers.Add(new ProviderConfig { Id = "box", BaseUrl = "http://127.0.0.1:1/v1", ApiKey = "sk-kept-key", Models = ["qwen"] });
        Assert.True(stored.Save(_engine.SettingsFile, replaceUnreadable: true));
        var (view, _) = _settings.Edit(As("ada"), null, _ => { });
        var typed = view!.Providers.Single(p => p.Name == "box").Config.Clone();
        typed.BaseUrl = "http://admin:hunter2@127.0.0.1:1/v1?key=sk-in-the-url&x=1#part";

        await view.ProviderCalls.CheckAsync(typed, "qwen");

        var entry = Assert.Single(_users.AuditTrail(), e => e.Action == "provider checked");
        Assert.Equal("box: http://127.0.0.1:1/v1 (with the credentials kept for it)", entry.Detail);
    }

    [Theory]
    [InlineData("https://user:pass@api.example.org:8443/v1/chat?token=t#f", "https://api.example.org:8443/v1/chat")]
    [InlineData("https://api.example.org/v1?key=k", "https://api.example.org/v1")]
    [InlineData("http://[::1]:5000/mcp", "http://[::1]:5000/mcp")]
    [InlineData("not an address", "(an address that could not be read)")]
    [InlineData("", "(an address that could not be read)")]
    [InlineData(null, "(an address that could not be read)")]
    public void Only_where_an_address_goes_is_audited(string? url, string audited)
        => Assert.Equal(audited, ServerSettings.Address(url));

    /// <summary>
    /// A provider's headers through the page's editor: named, never valued; left alone they are saved as they were, new
    /// ones replace them, and Remove takes them away. A key in Authorization is a provider's key like any other.
    /// </summary>
    [Fact]
    public async Task A_provider_s_headers_are_named_kept_replaced_and_removed_through_the_page()
    {
        var stored = AppSettings.Load(_engine.SettingsFile);
        stored.Providers.Add(new ProviderConfig { Id = "box", BaseUrl = "http://127.0.0.1:1/v1", Headers = new() { ["Authorization"] = "Bearer hdr-very-secret" } });
        Assert.True(stored.Save(_engine.SettingsFile, replaceUnreadable: true));

        async Task<Dictionary<string, string>> ApplyAndSave(Action<ProviderEditViewModel> edit)
        {
            var (view, _) = _settings.Edit(As("ada"), null, _ => { });
            var editor = new ProviderEditViewModel(view!.Providers.Single(p => p.Name == "box").Config, () => { }, view.ProviderCalls);
            Assert.EndsWith(": …", editor.HeaderNames);
            Assert.DoesNotContain("hdr-", editor.HeadersText + editor.HeaderNames);
            edit(editor);
            editor.SaveCommand.Execute(null);
            await view.SaveCommand.ExecuteAsync();
            return AppSettings.Load(_engine.SettingsFile).Providers.Single(p => p.Id == "box").Headers;
        }

        Assert.Equal("Bearer hdr-very-secret", (await ApplyAndSave(_ => { }))["Authorization"]);
        Assert.Equal(new Dictionary<string, string> { ["api-key"] = "hdr-new" },
            await ApplyAndSave(e => e.NewHeadersText = "api-key: hdr-new"));
        // Put back, then removed.
        await ApplyAndSave(e => e.NewHeadersText = "Authorization: Bearer hdr-very-secret");
        Assert.Empty(await ApplyAndSave(e => e.ForgetHeaders = true));
    }

    [Fact]
    public void The_workspace_roots_are_an_admin_s_to_read()
    {
        var settings = new ServerSettings(_users, _engine, new WorkspaceRoots([_root]));

        Assert.Equal([_root], settings.WorkspaceRoots(As("ada")).Roots!);
        Assert.Contains("role does not include", settings.WorkspaceRoots(As("alice")).Problem);
    }

    /// <summary>
    /// The standing write grants are the settings' to show (the window's rows) and an Admin's to revoke, through the
    /// service, audited - the view model the page edits writes no file on the service (editsFilesHere). An Operator, who
    /// can make a grant from a decision card, cannot take one back from the settings; an orphan goes whole, as the window's.
    /// </summary>
    [Fact]
    public void A_kept_grant_is_listed_and_only_an_admin_revokes_it_audited()
    {
        var grants = new Enactive.Core.Permissions.WritableRoots(Path.Combine(_root, "writable-roots.json"));
        var settings = new ServerSettings(_users, _engine, new WorkspaceRoots([_root]), grants);
        var workspace = Path.Combine(_root, "project");
        var gone = Path.Combine(_root, "forgotten");
        var outside = Path.Combine(_root, "shared");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        _engine.Workspaces.Touch(workspace);
        Assert.Null(grants.Add(workspace, outside));
        Assert.Null(grants.Add(gone, outside));

        var rows = settings.Edit(As("ada"), null, _ => { }).View!.WritableRootRows;
        Assert.Equal(2, rows.Count);
        var known = rows.Single(r => !r.IsOrphan);
        var orphan = rows.Single(r => r.IsOrphan);
        Assert.Equal(workspace, known.Workspace, ignoreCase: true);

        var (refused, why) = settings.RevokeWritableFolder(As("alice"), known.Key, known.Folder);
        Assert.False(refused);
        Assert.Contains("role does not include", why);
        Assert.NotEmpty(grants.For(workspace));

        Assert.True(settings.RevokeWritableFolder(As("ada"), known.Key, known.Folder).Revoked);
        Assert.Empty(grants.For(workspace));
        Assert.Contains(_users.AuditTrail(), e => e is { Account: "ada", Action: "writable folder revoked" } && e.Detail?.Contains(workspace, StringComparison.OrdinalIgnoreCase) == true);
        // Revoked since the page was drawn: said, not done twice.
        Assert.False(settings.RevokeWritableFolder(As("ada"), known.Key, known.Folder).Revoked);

        Assert.True(settings.RevokeWritableFolder(As("ada"), orphan.Key, orphan.Folder).Revoked);
        Assert.Empty(grants.Review([]));
    }

    /// <summary>An OpenAI-compatible endpoint that answers one request for its models, and says what it was sent.</summary>
    private sealed class OneAnswerProvider : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly Task _answering;

        public OneAnswerProvider()
        {
            _listener.Start();
            _answering = AnswerAsync();
        }

        public string BaseUrl => $"http://127.0.0.1:{((System.Net.IPEndPoint)_listener.LocalEndpoint).Port}/v1";

        public string? Authorization { get; private set; }

        private async Task AnswerAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var reader = new StreamReader(stream);
            for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
                if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                    Authorization = line["Authorization:".Length..].Trim();
            var body = "{\"data\":[{\"id\":\"qwen\"}]}";
            var answer = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(answer));
        }

        public void Dispose()
        {
            _listener.Stop();
            try { _answering.Wait(TimeSpan.FromSeconds(1)); } catch { /* stopped */ }
        }
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
