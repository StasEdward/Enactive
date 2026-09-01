using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AIClient.App.Ui;

/// <summary>Settings: endpoint, model (with a live list from Ollama), and global instructions.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public SettingsWindow(AppSettings settings, Action<AppSettings> onSaved)
    {
        Title = "Settings";
        Width = 660;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var baseUrlBox = new TextBox { Text = settings.BaseUrl, Watermark = "http://localhost:11434/v1" };
        var modelBox = new TextBox { Text = settings.Model, Watermark = "model name" };

        var modelsCombo = new ComboBox { PlaceholderText = "installed models…", HorizontalAlignment = HorizontalAlignment.Stretch };
        modelsCombo.SelectionChanged += (_, _) =>
        {
            if (modelsCombo.SelectedItem is string chosen && !string.IsNullOrEmpty(chosen))
                modelBox.Text = chosen;
        };

        var refreshButton = new Button { Content = "Refresh" };
        var status = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        refreshButton.Click += async (_, _) =>
        {
            status.Text = "loading…";
            try
            {
                var models = await FetchModelsAsync(baseUrlBox.Text ?? string.Empty);
                modelsCombo.ItemsSource = models;
                status.Text = models.Count == 0 ? "no models found" : $"{models.Count} model(s)";
            }
            catch (Exception ex)
            {
                status.Text = "error: " + ex.Message;
            }
        };

        var globalBox = new TextBox
        {
            Text = settings.GlobalInstructions,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 160,
            Watermark = "Global instructions — applied to every run"
        };

        var multiAgentBox = new CheckBox
        {
            Content = "Multi-agent: Anthropic reasoner plans + reviews, local model codes",
            IsChecked = settings.MultiAgent
        };
        var apiKeyBox = new TextBox { Text = settings.AnthropicApiKey, Watermark = "Anthropic API key (sk-ant-…)", PasswordChar = '•' };
        var reasonerModelBox = new TextBox { Text = settings.ReasonerModel, Watermark = "reasoner model, e.g. claude-3-5-sonnet-latest" };
        var workspaceIdBox = new TextBox { Text = settings.AnthropicWorkspaceId, Watermark = "Anthropic workspace id — only for identity-linked keys" };

        var reasonerModelsCombo = new ComboBox { PlaceholderText = "Anthropic models…", HorizontalAlignment = HorizontalAlignment.Stretch };
        reasonerModelsCombo.SelectionChanged += (_, _) =>
        {
            if (reasonerModelsCombo.SelectedItem is string id && !string.IsNullOrEmpty(id))
                reasonerModelBox.Text = id;
        };
        var reasonerRefresh = new Button { Content = "Refresh" };
        var reasonerStatus = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        reasonerRefresh.Click += async (_, _) =>
        {
            reasonerStatus.Text = "loading…";
            try
            {
                var models = await FetchAnthropicModelsAsync(apiKeyBox.Text ?? string.Empty, workspaceIdBox.Text ?? string.Empty);
                reasonerModelsCombo.ItemsSource = models;
                reasonerStatus.Text = models.Count == 0 ? "no models" : $"{models.Count} model(s)";
            }
            catch (Exception ex)
            {
                reasonerStatus.Text = "error: " + ex.Message;
            }
        };

        var saveButton = new Button { Content = "Save" };
        var cancelButton = new Button { Content = "Cancel" };
        saveButton.Click += (_, _) =>
        {
            settings.BaseUrl = (baseUrlBox.Text ?? string.Empty).Trim();
            settings.Model = (modelBox.Text ?? string.Empty).Trim();
            settings.GlobalInstructions = globalBox.Text ?? string.Empty;
            settings.MultiAgent = multiAgentBox.IsChecked == true;
            settings.AnthropicApiKey = (apiKeyBox.Text ?? string.Empty).Trim();
            settings.ReasonerModel = (reasonerModelBox.Text ?? string.Empty).Trim();
            settings.AnthropicWorkspaceId = (workspaceIdBox.Text ?? string.Empty).Trim();
            onSaved(settings);
            Close();
        };
        cancelButton.Click += (_, _) => Close();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    Label("Endpoint (OpenAI-compatible base URL)"),
                    baseUrlBox,
                    Label("Model"),
                    modelBox,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { modelsCombo, refreshButton } },
                    status,
                    Label("Global instructions"),
                    new TextBlock
                    {
                        Text = "Applied to all runs — preferences, conventions, or context the agent should always know.",
                        Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap
                    },
                    globalBox,
                    Label("Multi-agent (reasoner + coder)"),
                    new TextBlock
                    {
                        Text = "The Anthropic model plans and reviews each step (PASS/FAIL); the local model above writes the code.",
                        Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap
                    },
                    multiAgentBox,
                    apiKeyBox,
                    reasonerModelBox,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { reasonerModelsCombo, reasonerRefresh } },
                    reasonerStatus,
                    workspaceIdBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children = { saveButton, cancelButton }
                    }
                }
            }
        };
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        FontSize = 12,
        Margin = new Thickness(0, 10, 0, 2)
    };

    private async Task<List<string>> FetchModelsAsync(string baseUrl)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            root = root[..^3].TrimEnd('/');
        var url = root + "/api/tags";

        using var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();

        var list = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in models.EnumerateArray())
            {
                if (model.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    list.Add(name.GetString()!);
            }
        }
        return list;
    }

    private async Task<List<string>> FetchAnthropicModelsAsync(string apiKey, string workspaceId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=1000");
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        if (!string.IsNullOrWhiteSpace(workspaceId))
            request.Headers.TryAddWithoutValidation("anthropic-workspace-id", workspaceId);

        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();

        var list = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in data.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    list.Add(id.GetString()!);
            }
        }
        return list;
    }
}
