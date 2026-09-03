using System.Text.Json;

namespace AIClient.App.Ui;

/// <summary>Persisted app settings (model, endpoint, global instructions) — shared across all runs.</summary>
internal sealed class AppSettings
{
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "qwen2.5-coder";
    public string GlobalInstructions { get; set; } = string.Empty;

    // Ollama context window (options.num_ctx) for the coder model. Null = let Ollama use
    // whatever the model was already loaded with, which is how AIClient used to silently
    // inherit an 8K/64K context set by something else (ollama run, another client, ...).
    public int? NumCtx { get; set; }

    // Multi-agent: a reasoning model (Anthropic) plans + reviews; the local coder model executes.
    public bool MultiAgent { get; set; }

    // The API key is kept in memory as plaintext (the app uses it directly) but is NEVER written to
    // disk in the clear: Save() stores only the DPAPI-encrypted form in AnthropicApiKeyProtected and
    // blanks this field before serializing; Load() decrypts it back. A legacy plaintext value already
    // on disk is read here and migrated to the encrypted form on the next Save().
    public string AnthropicApiKey { get; set; } = string.Empty;

    // DPAPI-encrypted API key ("dpapi:"-prefixed base64) — this is what actually lives in settings.json.
    public string AnthropicApiKeyProtected { get; set; } = string.Empty;

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
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file));
                if (loaded is not null)
                {
                    // Encrypted form wins; a legacy plaintext AnthropicApiKey (no protected value) is kept
                    // as-is and will be encrypted on the next Save().
                    if (!string.IsNullOrEmpty(loaded.AnthropicApiKeyProtected))
                        loaded.AnthropicApiKey = Secret.Unprotect(loaded.AnthropicApiKeyProtected);
                    return loaded;
                }
            }
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

            // Persist only the encrypted key; blank the plaintext during serialization, restore after.
            AnthropicApiKeyProtected = Secret.Protect(AnthropicApiKey);
            var plaintext = AnthropicApiKey;
            AnthropicApiKey = string.Empty;
            try
            {
                File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                AnthropicApiKey = plaintext;
            }
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
