using AIClient.Core.Providers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AIClient.App.Ui;

/// <summary>Add/edit one provider endpoint. Writes back into <paramref name="config"/> only on Save.</summary>
internal sealed class ProviderEditWindow : Window
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public ProviderEditWindow(ProviderConfig config, Action onSaved)
    {
        Title = "Provider";
        Width = 620;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var idBox = new TextBox { Text = config.Id, Watermark = "id — e.g. ollama, anthropic, openrouter (used in providerId/model)" };
        var nameBox = new TextBox { Text = config.DisplayName, Watermark = "display name" };
        var kindBox = new ComboBox
        {
            ItemsSource = Enum.GetValues<ProviderKind>(),
            SelectedItem = config.Kind,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var baseUrlBox = new TextBox { Text = config.BaseUrl, Watermark = "base URL — e.g. http://localhost:11434/v1" };
        var apiKeyBox = new TextBox { Text = config.ApiKey, Watermark = "API key (blank for local providers)", PasswordChar = '•' };
        var maxTokensBox = new TextBox
        {
            Text = config.MaxTokens?.ToString() ?? string.Empty,
            Watermark = "max output tokens — blank = auto (adjusts to the model's cap)"
        };
        var headersBox = new TextBox
        {
            Text = ModelFetch.FormatHeaders(config.Headers),
            Watermark = "extra headers — one \"Header-Name: value\" per line",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60
        };
        var modelsBox = new TextBox
        {
            Text = string.Join("\n", config.Models),
            Watermark = "models — one per line",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 100
        };

        var fetchedCombo = new ComboBox { PlaceholderText = "fetched models…", HorizontalAlignment = HorizontalAlignment.Stretch };
        fetchedCombo.SelectionChanged += (_, _) =>
        {
            if (fetchedCombo.SelectedItem is not string picked || string.IsNullOrEmpty(picked))
                return;
            var lines = (modelsBox.Text ?? string.Empty).Replace("\r\n", "\n").Split('\n')
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (!lines.Contains(picked))
                lines.Add(picked);
            modelsBox.Text = string.Join("\n", lines);
        };
        var refreshButton = new Button { Content = "Fetch" };
        var status = new TextBlock { Foreground = Brushes.Gray, FontSize = 11 };
        refreshButton.Click += async (_, _) =>
        {
            status.Text = "loading…";
            try
            {
                var kind = kindBox.SelectedItem is ProviderKind k ? k : ProviderKind.OpenAiCompatible;
                var models = kind == ProviderKind.Anthropic
                    ? await ModelFetch.AnthropicAsync(_http, apiKeyBox.Text ?? string.Empty,
                        ModelFetch.ParseHeaders(headersBox.Text).TryGetValue("anthropic-workspace-id", out var wid) ? wid : null)
                    : await ModelFetch.OllamaAsync(_http, baseUrlBox.Text ?? string.Empty);
                fetchedCombo.ItemsSource = models;
                status.Text = models.Count == 0 ? "no models" : $"{models.Count} model(s) — pick to add";
            }
            catch (Exception ex)
            {
                status.Text = "error: " + ex.Message;
            }
        };

        var saveButton = new Button { Content = "Save" };
        var cancelButton = new Button { Content = "Cancel" };
        saveButton.Click += (_, _) =>
        {
            config.Id = (idBox.Text ?? string.Empty).Trim();
            config.DisplayName = (nameBox.Text ?? string.Empty).Trim();
            config.Kind = kindBox.SelectedItem is ProviderKind k ? k : ProviderKind.OpenAiCompatible;
            config.BaseUrl = (baseUrlBox.Text ?? string.Empty).Trim();
            config.ApiKey = (apiKeyBox.Text ?? string.Empty).Trim();
            config.MaxTokens = int.TryParse((maxTokensBox.Text ?? string.Empty).Trim(), out var mt) && mt > 0 ? mt : null;
            config.Headers = ModelFetch.ParseHeaders(headersBox.Text);
            config.Models = (modelsBox.Text ?? string.Empty).Replace("\r\n", "\n").Split('\n')
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
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
                    Label("Display name"), nameBox,
                    Label("Kind"), kindBox,
                    Label("Base URL"), baseUrlBox,
                    Label("API key"), apiKeyBox,
                    Label("Max output tokens"), maxTokensBox,
                    Label("Headers"), headersBox,
                    Label("Models"), modelsBox,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { fetchedCombo, refreshButton } },
                    status,
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
