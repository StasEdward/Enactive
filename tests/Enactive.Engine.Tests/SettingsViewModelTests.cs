namespace Enactive.Engine.Tests;

using System.Globalization;
using Enactive.Application;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Providers;
using Enactive.Settings;

/// <summary>
/// The settings' view model, shared by the window and the service's settings page: what a host hands it, and what it
/// does with a copy of the settings that holds no secret (AppSettings.WithoutSecrets), which is all the service's page
/// is ever given.
/// </summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "enactive-settings-vm", Guid.NewGuid().ToString("N"));
    private AppSettings? _saved;

    public SettingsViewModelTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>
    /// A view model over these settings, reading writable folders and workspaces of its own - never the machine's
    /// %APPDATA% - and keeping what Save hands over.
    /// </summary>
    private SettingsViewModel Open(AppSettings settings, IStartupEntry? startup = null,
        Func<AppSettings, IHostSettingsPane>? hostPane = null, IProviderCalls? calls = null)
        => new(settings, s => _saved = s, startup ?? NoStartupEntry.Instance, editsFilesHere: true, hostPane: hostPane, providerCalls: calls,
            writableRoots: new WritableRoots(Path.Combine(_folder, "writable-roots.json")),
            knownWorkspaces: () => []);

    /// <summary>Settings with a provider key and a mail password, saved and read back - then made into a page's copy.</summary>
    private AppSettings PageCopy()
    {
        var settings = new AppSettings();
        settings.Providers.Add(new ProviderConfig { Id = "cloud", Kind = ProviderKind.Anthropic, BaseUrl = "https://api.example.com", ApiKey = "sk-very-secret", Models = ["big"] });
        settings.Smtp.Host = "mail.example.com";
        settings.Smtp.Password = "mail-very-secret";
        var file = Path.Combine(_folder, "settings.json");
        Assert.True(settings.Save(file, replaceUnreadable: true), settings.LastSaveError);
        return AppSettings.Load(file).WithoutSecrets();
    }

    // ── the window's own pane ─────────────────────────────────────────────

    private sealed class Pane : IHostSettingsPane
    {
        private bool _busy;
        public bool Busy
        {
            get => _busy;
            set { _busy = value; BusyChanged?.Invoke(); }
        }
        public event Action? BusyChanged;
        public int Shown;
        public void Save(AppSettings working) => working.RemoteAccess.Enabled = true;
        void IHostSettingsPane.Shown() => Shown++;
    }

    /// <summary>
    /// Remote access is the window's pane, beside the shared settings: Save waits while it applies a code - settings and
    /// keys for different computers otherwise - and lets it write its switch.
    /// </summary>
    [Fact]
    public async Task Save_waits_for_the_host_s_pane_and_lets_it_write_itself()
    {
        Pane pane = null!;
        var vm = Open(new AppSettings(), hostPane: _ => pane = new Pane());
        var told = 0;
        vm.SaveCommand.CanExecuteChanged += (_, _) => told++;

        pane.Busy = true;
        Assert.Equal((1, false), (told, vm.SaveCommand.CanExecute(null)));
        // Pressed anyway - a key, a second binding: a disabled button is a promise only the button keeps.
        await vm.SaveCommand.ExecuteAsync();
        Assert.Null(_saved);
        pane.Busy = false;
        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.ShowRemoteCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync();

        Assert.Equal(1, pane.Shown);
        Assert.True(vm.IsRemote);
        Assert.True(_saved!.RemoteAccess.Enabled);
    }

    /// <summary>
    /// The evidence budget is automatic unless a person set one: the box is blank for it and says what blank comes to -
    /// from the window the provider bound to Review declares, and again whenever Review changes - and blank is saved back
    /// as automatic, a number as that number.
    /// </summary>
    [Fact]
    public async Task A_blank_evidence_budget_says_what_it_comes_to_and_is_saved_as_automatic()
    {
        var settings = new AppSettings();
        settings.Providers.Add(new ProviderConfig { Id = "cloud", Kind = ProviderKind.Anthropic, BaseUrl = "https://api.example.com", Models = ["big"], ContextWindowTokens = 1_000_000 });
        settings.Providers.Add(new ProviderConfig { Id = "local", Kind = ProviderKind.OllamaNative, BaseUrl = "http://localhost:11434", Models = ["small"] });
        var vm = Open(settings);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("", vm.EvidenceBudgetText);
        Assert.Equal("auto - no model is bound to Review", vm.EvidenceBudgetAuto);
        vm.Review = "cloud/big";
        Assert.Contains(nameof(SettingsViewModel.EvidenceBudgetAuto), changed);
        Assert.Equal("auto - up to 72,000 characters, from cloud/big's 1,000,000-token window", vm.EvidenceBudgetAuto);
        vm.Review = "local/small";
        Assert.Equal("auto - 6,000 characters: local declares no context window for small", vm.EvidenceBudgetAuto);

        var plain = Open(new AppSettings());
        await plain.SaveCommand.ExecuteAsync();
        Assert.Null(_saved!.Engine.EvidenceBudget);

        var set = Open(new AppSettings());
        set.EvidenceBudgetText = "9000";
        await set.SaveCommand.ExecuteAsync();
        Assert.Equal(9000, _saved!.Engine.EvidenceBudget);

        var reopened = Open(_saved);
        Assert.Equal("9000", reopened.EvidenceBudgetText);
        reopened.EvidenceBudgetText = "";
        await reopened.SaveCommand.ExecuteAsync();
        Assert.Null(_saved!.Engine.EvidenceBudget);
    }

    /// <summary>
    /// A host with no start-up of its own (the service) is offered none, and is not told that "only Windows" starts
    /// programs this way - the line the window shows on a desktop that cannot, which on the service's page was the note
    /// under every Save.
    /// </summary>
    [Fact]
    public async Task A_host_without_start_up_is_offered_none_and_told_nothing_of_it()
    {
        var vm = Open(new AppSettings());
        await vm.SaveCommand.ExecuteAsync();

        Assert.False(vm.StartupSupported);
        Assert.Equal("", vm.StartupNote);
        Assert.NotNull(_saved);
    }

    // ── a copy that holds no secret ───────────────────────────────────────

    /// <summary>
    /// A mail password kept is said as kept, not as one this account cannot read: on the copy, every password stored
    /// has an empty plaintext beside it, which is what the window took for "unreadable".
    /// </summary>
    [WindowsFact]
    public void A_password_kept_reads_as_kept_not_as_unreadable()
    {
        var vm = Open(PageCopy());

        Assert.Equal("", vm.SmtpPassword);
        Assert.True(vm.SmtpPasswordStored);
        Assert.DoesNotContain("cannot read", vm.SmtpState);
    }

    /// <summary>An empty box keeps the password; Remove takes away the plaintext and the stored form both.</summary>
    [WindowsFact]
    public async Task An_empty_password_box_keeps_it_and_remove_takes_it_away()
    {
        var kept = Open(PageCopy());
        await kept.SaveCommand.ExecuteAsync();
        Assert.True(AppSettings.IsKept(_saved!.Smtp.PasswordProtected));

        var removed = Open(PageCopy());
        removed.SmtpForgetPassword = true;
        Assert.False(removed.SmtpPasswordStored);
        await removed.SaveCommand.ExecuteAsync();
        Assert.Equal(("", ""), (_saved!.Smtp.Password, _saved.Smtp.PasswordProtected));
    }

    /// <summary>
    /// The provider editor on the copy: the box is empty, it says a key is kept, Test asks with the kept key's stored
    /// form for the host to look up - not with no key, which lit a provider that is fine up red - and Remove takes the
    /// key away on Apply.
    /// </summary>
    [WindowsFact]
    public async Task The_provider_editor_says_a_key_is_kept_asks_with_it_and_removes_it_on_purpose()
    {
        var config = PageCopy().Providers[0];
        var calls = new Calls();
        var editor = new ProviderEditViewModel(config, () => { }, calls);

        Assert.Equal("", editor.ApiKey);
        Assert.True(editor.KeyStored);
        await editor.TestCommand.ExecuteAsync();
        Assert.Equal(config.ApiKeyProtected, calls.Asked.Single().ApiKeyProtected);

        editor.ForgetKey = true;
        editor.SaveCommand.Execute(null);
        Assert.Equal(("", ""), (config.ApiKey, config.ApiKeyProtected));
    }

    /// <summary>A key typed in the editor is the new key, and Test asks with it rather than the kept one.</summary>
    [WindowsFact]
    public async Task A_key_typed_in_the_editor_replaces_the_kept_one()
    {
        var config = PageCopy().Providers[0];
        var calls = new Calls();
        var editor = new ProviderEditViewModel(config, () => { }, calls) { ApiKey = "sk-new" };

        Assert.False(editor.KeyStored);
        await editor.TestCommand.ExecuteAsync();
        editor.SaveCommand.Execute(null);

        Assert.Equal(("sk-new", ""), (calls.Asked.Single().ApiKey, calls.Asked.Single().ApiKeyProtected));
        Assert.Equal("sk-new", config.ApiKey);
    }

    /// <summary>The lights on the Providers list are asked of the host's calls, once, when the list is first opened.</summary>
    [Fact]
    public async Task The_providers_lights_ask_the_host_s_calls_once()
    {
        var settings = new AppSettings();
        settings.Providers.Add(new ProviderConfig { Id = "box", BaseUrl = "http://127.0.0.1:1/v1", Models = ["qwen"] });
        var calls = new Calls();
        var vm = Open(settings, calls: calls);

        vm.ShowAiProvidersCommand.Execute(null);
        vm.ShowAiGeneralCommand.Execute(null);
        vm.ShowAiProvidersCommand.Execute(null);
        await calls.Done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("box", calls.Asked.Single().Id);
        Assert.Equal(ProviderHealth.Ready, vm.Providers.Single().Health.Health);
        Assert.Same(calls, vm.ProviderCalls);
    }

    /// <summary>The time a light was learned reads the same on every machine; "HH:mm:ss" took the culture's separator.</summary>
    [Fact]
    public void When_a_provider_was_checked_reads_the_same_under_any_culture()
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
            var at = new DateTimeOffset(2026, 10, 12, 9, 5, 7, TimeSpan.Zero);
            var row = new ProviderRow(new ProviderConfig { Id = "box" }, _ => { }, _ => { })
            {
                Health = new ProviderStatus(ProviderHealth.Ready, "Ready", [], at)
            };

            Assert.EndsWith("Checked at " + at.ToLocalTime().ToString("HH':'mm':'ss", CultureInfo.InvariantCulture), row.HealthTip);
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    /// <summary>The Writable folders pane reads the grants and workspaces a host names, not the machine's.</summary>
    [Fact]
    public void Writable_folders_are_read_where_the_host_says()
    {
        var workspace = Path.Combine(_folder, "project");
        var outside = Path.Combine(_folder, "elsewhere");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        var roots = new WritableRoots(Path.Combine(_folder, "writable-roots.json"));
        Assert.Null(roots.Add(workspace, outside));

        var vm = new SettingsViewModel(new AppSettings(), _ => { }, NoStartupEntry.Instance, editsFilesHere: true,
            writableRoots: roots, knownWorkspaces: () => [workspace]);

        var row = Assert.Single(vm.WritableRootRows);
        Assert.False(row.IsOrphan);
        Assert.EndsWith("project", row.Workspace);
        Assert.EndsWith("elsewhere", row.Folder);
    }

    /// <summary>
    /// A host whose templates and grants are changed elsewhere, as the person (the service), is given a view model whose
    /// Templates and Writable folders write nothing: New, Remove and Revoke do nothing there, so a control a page wires
    /// to them later cannot write past the host's own checks. The window's still do.
    /// </summary>
    [Fact]
    public void A_host_that_edits_no_files_here_removes_no_template_and_revokes_no_grant()
    {
        var workspace = Path.Combine(_folder, "project");
        var outside = Path.Combine(_folder, "elsewhere");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        var store = new Enactive.Workspace.TemplateStore(workspace);
        store.Save(Enactive.Workspace.TemplateStore.FromJson("""{ "id": "weekly", "name": "Weekly", "goal": "Sum up the week." }""", out _)!,
            Enactive.Workspace.TemplateScope.Workspace);
        var roots = new WritableRoots(Path.Combine(_folder, "writable-roots.json"));
        Assert.Null(roots.Add(workspace, outside));

        var vm = new SettingsViewModel(new AppSettings(), _ => { }, NoStartupEntry.Instance, editsFilesHere: false,
            workspaceRoot: workspace, writableRoots: roots, knownWorkspaces: () => [workspace]);
        var asked = 0;
        vm.ConfirmRequested += (_, _) => { asked++; return Task.FromResult(true); };
        vm.TemplateEditRequested += (_, _, _, _) => asked++;

        vm.AddTemplateCommand.Execute(null);
        var weekly = vm.Templates.Single(t => t.Template.Id == "weekly" && t.Origin == "This workspace");
        weekly.EditCommand.Execute(null);
        weekly.RemoveCommand.Execute(null);
        vm.WritableRootRows.Single().RevokeCommand.Execute(null);

        Assert.Equal(0, asked);
        Assert.NotNull(store.Find("weekly"));
        Assert.Single(roots.For(workspace));

        // The window's: the same New asks for the editor, so the switch is what stopped it above.
        var window = new SettingsViewModel(new AppSettings(), _ => { }, NoStartupEntry.Instance, editsFilesHere: true,
            workspaceRoot: workspace, writableRoots: roots, knownWorkspaces: () => [workspace]);
        window.TemplateEditRequested += (_, _, _, _) => asked++;
        window.AddTemplateCommand.Execute(null);
        Assert.Equal(1, asked);
    }

    /// <summary>The build's dates read the same under any culture: "HH:mm" took the culture's time separator.</summary>
    [Fact]
    public void The_build_s_dates_are_the_same_under_any_culture()
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
            var date = new System.Text.RegularExpressions.Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$");

            Assert.Matches(date, BuildInfo.BuiltAt);
            var components = BuildInfo.Components();
            Assert.NotEmpty(components);
            Assert.All(components.Where(c => c.Detail.Length > 0), c => Assert.Matches(date, c.Detail));
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    /// <summary>A provider asked: what it was asked with, and Ready for every one.</summary>
    private sealed class Calls : IProviderCalls
    {
        public List<ProviderConfig> Asked { get; } = [];
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProviderStatus> CheckAsync(ProviderConfig provider, string? model)
        {
            lock (Asked)
                Asked.Add(provider);
            Done.TrySetResult();
            return Task.FromResult(new ProviderStatus(ProviderHealth.Ready, "Ready", [], DateTimeOffset.UtcNow));
        }

        public Task<List<string>> ModelsAsync(ProviderConfig provider) => Task.FromResult(new List<string>());
    }
}
