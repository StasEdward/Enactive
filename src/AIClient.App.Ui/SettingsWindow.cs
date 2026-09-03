using AIClient.Core.Providers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace AIClient.App.Ui;

/// <summary>
/// Settings as tabs over the universal team schema (Docs/MODELS.md): General, Providers (any number of
/// endpoints), Team (editable workers, each with its own model), and Phases (which model runs Plan / Review).
/// Edits a throwaway clone; only Save (which calls <c>onSaved</c>) commits them.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly AppSettings _working;

    public SettingsWindow(AppSettings settings, Action<AppSettings> onSaved)
    {
        _working = settings.Clone();

        Title = "Settings";
        Width = 720;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "General", Content = BuildGeneralTab() });
        tabs.Items.Add(new TabItem { Header = "Providers", Content = BuildProvidersTab() });
        tabs.Items.Add(new TabItem { Header = "Team", Content = BuildTeamTab() });
        tabs.Items.Add(new TabItem { Header = "Phases", Content = BuildPhasesTab() });

        var saveButton = new Button { Content = "Save" };
        var cancelButton = new Button { Content = "Cancel" };
        saveButton.Click += (_, _) =>
        {
            CommitGeneral();
            CommitPhases();
            onSaved(_working);
            Close();
        };
        cancelButton.Click += (_, _) => Close();

        var buttonBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { saveButton, cancelButton }
        };
        DockPanel.SetDock(buttonBar, Dock.Bottom);

        Content = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(12),
            Children = { buttonBar, tabs }
        };
    }

    // ── General ───────────────────────────────────────────────────────────────
    private TextBox _numCtxBox = null!;
    private TextBox _globalBox = null!;

    private Control BuildGeneralTab()
    {
        _numCtxBox = new TextBox
        {
            Text = _working.NumCtx?.ToString() ?? string.Empty,
            Watermark = "e.g. 8192 — blank uses whatever the model already has loaded"
        };
        _globalBox = new TextBox
        {
            Text = _working.GlobalInstructions,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 220,
            Watermark = "Global instructions — applied to every run"
        };

        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 6,
                Children =
                {
                    Header("Context length (num_ctx)"),
                    Hint("How much context to load Ollama models with. Bigger costs VRAM and can spill onto the "
                        + "CPU (check `ollama ps`). Leave blank to not override it."),
                    _numCtxBox,
                    Header("Global instructions"),
                    Hint("Applied to every run and appended to every worker's instructions."),
                    _globalBox
                }
            }
        };
    }

    private void CommitGeneral()
    {
        _working.NumCtx = int.TryParse((_numCtxBox.Text ?? string.Empty).Trim(), out var n) ? n : null;
        _working.GlobalInstructions = _globalBox.Text ?? string.Empty;
    }

    // ── Providers ──────────────────────────────────────────────────────────────
    private ListBox _providersList = null!;

    private Control BuildProvidersTab()
    {
        _providersList = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<ProviderConfig>((p, _) => new TextBlock
            {
                Text = p is null ? string.Empty : $"{p.Id}   ·   {p.Kind}   ·   {p.BaseUrl}",
                Margin = new Thickness(2)
            })
        };
        RefreshProviders();

        var addButton = new Button { Content = "Add" };
        var editButton = new Button { Content = "Edit" };
        var removeButton = new Button { Content = "Remove" };
        addButton.Click += (_, _) =>
        {
            var cfg = new ProviderConfig { Kind = ProviderKind.OpenAiCompatible };
            new ProviderEditWindow(cfg, () => { _working.Providers.Add(cfg); RefreshProviders(); }).Show(this);
        };
        editButton.Click += (_, _) =>
        {
            var idx = _providersList.SelectedIndex;
            if (idx >= 0 && idx < _working.Providers.Count)
                new ProviderEditWindow(_working.Providers[idx], RefreshProviders).Show(this);
        };
        removeButton.Click += (_, _) =>
        {
            var idx = _providersList.SelectedIndex;
            if (idx >= 0 && idx < _working.Providers.Count)
            {
                _working.Providers.RemoveAt(idx);
                RefreshProviders();
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0),
            Children = { addButton, editButton, removeButton }
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        return new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(12),
            Children = { buttons, _providersList }
        };
    }

    private void RefreshProviders()
    {
        var keep = _providersList.SelectedIndex;
        _providersList.ItemsSource = _working.Providers.ToList();
        _providersList.SelectedIndex = keep >= 0 && keep < _working.Providers.Count ? keep : -1;
    }

    // ── Team ────────────────────────────────────────────────────────────────────
    private ListBox _workersList = null!;

    private Control BuildTeamTab()
    {
        _workersList = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<WorkerConfig>((w, _) => new TextBlock
            {
                Text = w is null ? string.Empty : $"{w.Role}   ·   {w.Model}   ·   {w.Level}",
                Margin = new Thickness(2)
            })
        };
        RefreshWorkers();

        var addButton = new Button { Content = "Add" };
        var editButton = new Button { Content = "Edit" };
        var removeButton = new Button { Content = "Remove" };
        addButton.Click += (_, _) =>
        {
            var cfg = new WorkerConfig();
            new WorkerEditWindow(cfg, _working.ModelCatalog(), () => { _working.Workers.Add(cfg); RefreshWorkers(); }).Show(this);
        };
        editButton.Click += (_, _) =>
        {
            var idx = _workersList.SelectedIndex;
            if (idx >= 0 && idx < _working.Workers.Count)
                new WorkerEditWindow(_working.Workers[idx], _working.ModelCatalog(), RefreshWorkers).Show(this);
        };
        removeButton.Click += (_, _) =>
        {
            var idx = _workersList.SelectedIndex;
            if (idx >= 0 && idx < _working.Workers.Count)
            {
                _working.Workers.RemoveAt(idx);
                RefreshWorkers();
            }
        };

        var hint = Hint("The main window's role picker chooses which worker handles a run. Honesty rules and "
                      + "global instructions are added to every worker automatically.");
        DockPanel.SetDock(hint, Dock.Top);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0),
            Children = { addButton, editButton, removeButton }
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        return new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(12),
            Children = { hint, buttons, _workersList }
        };
    }

    private void RefreshWorkers()
    {
        var keep = _workersList.SelectedIndex;
        _workersList.ItemsSource = _working.Workers.ToList();
        _workersList.SelectedIndex = keep >= 0 && keep < _working.Workers.Count ? keep : -1;
    }

    // ── Phases ──────────────────────────────────────────────────────────────────
    private TextBox _planBox = null!;
    private TextBox _reviewBox = null!;
    private TextBox _lightBox = null!;
    private TextBox _heavyBox = null!;

    private Control BuildPhasesTab()
    {
        var catalog = new List<string> { "(none)" };
        catalog.AddRange(_working.ModelCatalog());

        _planBox = new TextBox { Text = _working.Bindings.Plan, Watermark = "providerId/model — blank = plan on the executing model" };
        var planCombo = new ComboBox { ItemsSource = catalog, PlaceholderText = "pick…", HorizontalAlignment = HorizontalAlignment.Stretch };
        planCombo.SelectionChanged += (_, _) =>
        {
            if (planCombo.SelectedItem is string m)
                _planBox.Text = m == "(none)" ? string.Empty : m;
        };

        _reviewBox = new TextBox { Text = _working.Bindings.Review, Watermark = "providerId/model — blank = no review (single-agent)" };
        var reviewCombo = new ComboBox { ItemsSource = catalog, PlaceholderText = "pick…", HorizontalAlignment = HorizontalAlignment.Stretch };
        reviewCombo.SelectionChanged += (_, _) =>
        {
            if (reviewCombo.SelectedItem is string m)
                _reviewBox.Text = m == "(none)" ? string.Empty : m;
        };

        _lightBox = new TextBox { Text = _working.Bindings.ExecuteLight, Watermark = "providerId/model — blank = the worker's own model" };
        var lightCombo = new ComboBox { ItemsSource = catalog, PlaceholderText = "pick…", HorizontalAlignment = HorizontalAlignment.Stretch };
        lightCombo.SelectionChanged += (_, _) =>
        {
            if (lightCombo.SelectedItem is string m)
                _lightBox.Text = m == "(none)" ? string.Empty : m;
        };

        _heavyBox = new TextBox { Text = _working.Bindings.ExecuteHeavy, Watermark = "providerId/model — blank = the worker's own model" };
        var heavyCombo = new ComboBox { ItemsSource = catalog, PlaceholderText = "pick…", HorizontalAlignment = HorizontalAlignment.Stretch };
        heavyCombo.SelectionChanged += (_, _) =>
        {
            if (heavyCombo.SelectedItem is string m)
                _heavyBox.Text = m == "(none)" ? string.Empty : m;
        };

        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 6,
                Children =
                {
                    Hint("Execute always runs on the selected worker's own model. Plan and Review use the models "
                        + "bound here. Leave Plan blank to plan on the executing model; leave Review blank to skip "
                        + "review entirely (single-agent)."),
                    Header("Plan model"),
                    _planBox,
                    planCombo,
                    Header("Review model"),
                    _reviewBox,
                    reviewCombo,
                    Hint("Per-step auto-routing (optional): the planner rates each step trivial / normal / complex. "
                        + "Trivial steps run on the light model, complex steps on the heavy model; normal steps stay on "
                        + "the worker's own model. Leave both blank to disable auto-routing."),
                    Header("Execute · light (trivial steps)"),
                    _lightBox,
                    lightCombo,
                    Header("Execute · heavy (complex steps)"),
                    _heavyBox,
                    heavyCombo
                }
            }
        };
    }

    private void CommitPhases()
    {
        _working.Bindings.Plan = (_planBox.Text ?? string.Empty).Trim();
        _working.Bindings.Review = (_reviewBox.Text ?? string.Empty).Trim();
        _working.Bindings.ExecuteLight = (_lightBox.Text ?? string.Empty).Trim();
        _working.Bindings.ExecuteHeavy = (_heavyBox.Text ?? string.Empty).Trim();
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text, FontWeight = FontWeight.Bold, FontSize = 12, Margin = new Thickness(0, 10, 0, 2)
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text, Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4)
    };
}
