using AIClient.Core.Permissions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AIClient.App.Ui;

/// <summary>Add/edit one team member (role + tools + level + its own model). Writes back only on Save.</summary>
internal sealed class WorkerEditWindow : Window
{
    private static readonly string[] KnownTools =
        { "write_file", "read_file", "list_dir", "run_command", "run_powershell", "git", "docker" };

    public WorkerEditWindow(WorkerConfig config, IReadOnlyList<string> modelCatalog, Action onSaved)
    {
        Title = "Team member";
        Width = 640;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var idBox = new TextBox { Text = config.Id, Watermark = "id — unique, e.g. developer, architect" };
        var roleBox = new TextBox { Text = config.Role, Watermark = "role name shown in the picker" };
        var instructionsBox = new TextBox
        {
            Text = config.Instructions,
            Watermark = "base instructions (honesty rules + global instructions are added automatically)",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 160
        };

        // Tools: the known tools plus any extra already on this worker.
        var toolNames = KnownTools.Concat(config.Tools.Where(t => !KnownTools.Contains(t))).Distinct().ToList();
        var toolChecks = new List<CheckBox>();
        var toolsPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var t in toolNames)
        {
            var cb = new CheckBox { Content = t, IsChecked = config.Tools.Contains(t), Margin = new Thickness(0, 0, 12, 0) };
            toolChecks.Add(cb);
            toolsPanel.Children.Add(cb);
        }

        var levelBox = new ComboBox
        {
            ItemsSource = Enum.GetValues<PermissionLevel>(),
            SelectedItem = config.Level,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var modelBox = new TextBox { Text = config.Model, Watermark = "providerId/model" };
        var modelCombo = new ComboBox { ItemsSource = modelCatalog, PlaceholderText = "pick a model…", HorizontalAlignment = HorizontalAlignment.Stretch };
        modelCombo.SelectionChanged += (_, _) =>
        {
            if (modelCombo.SelectedItem is string m && !string.IsNullOrEmpty(m))
                modelBox.Text = m;
        };

        var fallbackBox = new TextBox { Text = config.Fallback ?? string.Empty, Watermark = "fallback providerId/model (optional)" };
        var fallbackCombo = new ComboBox { ItemsSource = modelCatalog, PlaceholderText = "pick a fallback…", HorizontalAlignment = HorizontalAlignment.Stretch };
        fallbackCombo.SelectionChanged += (_, _) =>
        {
            if (fallbackCombo.SelectedItem is string m && !string.IsNullOrEmpty(m))
                fallbackBox.Text = m;
        };

        var saveButton = new Button { Content = "Save" };
        var cancelButton = new Button { Content = "Cancel" };
        saveButton.Click += (_, _) =>
        {
            config.Id = (idBox.Text ?? string.Empty).Trim();
            config.Role = (roleBox.Text ?? string.Empty).Trim();
            config.Instructions = instructionsBox.Text ?? string.Empty;
            config.Tools = toolChecks.Where(c => c.IsChecked == true).Select(c => (string)c.Content!).ToList();
            config.Level = levelBox.SelectedItem is PermissionLevel lvl ? lvl : PermissionLevel.Execute;
            config.Model = (modelBox.Text ?? string.Empty).Trim();
            var fb = (fallbackBox.Text ?? string.Empty).Trim();
            config.Fallback = fb.Length == 0 ? null : fb;
            onSaved();
            Close();
        };
        cancelButton.Click += (_, _) => Close();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 6,
                Children =
                {
                    Label("Id"), idBox,
                    Label("Role"), roleBox,
                    Label("Instructions"), instructionsBox,
                    Label("Tools"), toolsPanel,
                    Label("Permission level"), levelBox,
                    Label("Model"), modelBox,
                    modelCombo,
                    Label("Fallback model"), fallbackBox,
                    fallbackCombo,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0),
                        Children = { saveButton, cancelButton }
                    }
                }
            }
        };
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, FontWeight = FontWeight.Bold, FontSize = 12, Margin = new Thickness(0, 8, 0, 2)
    };
}
