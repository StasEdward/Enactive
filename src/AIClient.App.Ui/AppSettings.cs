using System.Text.Json;

namespace AIClient.App.Ui;

/// <summary>Persisted app settings (model, endpoint, global instructions) — shared across all runs.</summary>
internal sealed class AppSettings
{
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "qwen2.5-coder";
    public string GlobalInstructions { get; set; } = string.Empty;

    // Multi-agent: a reasoning model (Anthropic) plans + reviews; the local coder model executes.
    public bool MultiAgent { get; set; }
    public string AnthropicApiKey { get; set; } = string.Empty;
    public string ReasonerModel { get; set; } = "claude-3-5-sonnet-latest";
    public string AnthropicWorkspaceId { get; set; } = string.Empty;

    // Main window placement (restored on start).
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    private static string SettingsFile()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIClient", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var file = SettingsFile();
            if (File.Exists(file))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? SeedFromEnvironment();
        }
        catch { /* ignore */ }

        return SeedFromEnvironment();
    }

    public void Save()
    {
        try
        {
            var file = SettingsFile();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore */ }
    }

    private static AppSettings SeedFromEnvironment() => new()
    {
        BaseUrl = Environment.GetEnvironmentVariable("AICLIENT_OLLAMA_URL") ?? "http://localhost:11434/v1",
        Model = Environment.GetEnvironmentVariable("AICLIENT_MODEL") ?? "qwen2.5-coder",
        GlobalInstructions = string.Empty
    };
}
