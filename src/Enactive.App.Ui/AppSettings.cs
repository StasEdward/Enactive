using System.Text.Json;
using System.Text.Json.Serialization;
using Enactive.Agents;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;

namespace Enactive.App.Ui;

/// <summary>One configured provider endpoint as persisted in settings.json (Docs/MODELS.md).</summary>
internal sealed class ProviderConfig
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ProviderKind Kind { get; set; } = ProviderKind.OpenAiCompatible;
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>DPAPI-encrypted API key ("dpapi:"-prefixed) — this is what lives on disk.</summary>
    public string ApiKeyProtected { get; set; } = string.Empty;

    /// <summary>Plaintext key held only in memory; never serialized (decrypted from <see cref="ApiKeyProtected"/> on load).</summary>
    [JsonIgnore] public string ApiKey { get; set; } = string.Empty;

    public Dictionary<string, string> Headers { get; set; } = new();
    public List<string> Models { get; set; } = new();

    /// <summary>Max output tokens for this provider (blank = the provider's built-in default). Anthropic max_tokens.</summary>
    public int? MaxTokens { get; set; }

    public ProviderConfig Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Kind = Kind,
        BaseUrl = BaseUrl,
        ApiKeyProtected = ApiKeyProtected,
        ApiKey = ApiKey,
        Headers = new Dictionary<string, string>(Headers),
        Models = new List<string>(Models),
        MaxTokens = MaxTokens
    };
}

/// <summary>One team member (role + its own model) as persisted in settings.json.</summary>
internal sealed class WorkerConfig
{
    public string Id { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public List<string> Tools { get; set; } = new();
    public PermissionLevel Level { get; set; } = PermissionLevel.Execute;

    /// <summary>The worker's model as "providerId/model".</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Optional fallback model as "providerId/model", or null.</summary>
    public string? Fallback { get; set; }

    public WorkerConfig Clone() => new()
    {
        Id = Id,
        Role = Role,
        Instructions = Instructions,
        Tools = new List<string>(Tools),
        Level = Level,
        Model = Model,
        Fallback = Fallback
    };
}

/// <summary>Which model runs each orchestration phase. Empty Plan = plan on the executing model; empty Review = no review.</summary>
internal sealed class PhaseBindings
{
    public string Plan { get; set; } = string.Empty;
    public string Review { get; set; } = string.Empty;

    /// <summary>Execute model for Trivial steps (per-step auto-routing); blank = the worker's own model.</summary>
    public string ExecuteLight { get; set; } = string.Empty;

    /// <summary>Execute model for Complex steps (per-step auto-routing); blank = the worker's own model.</summary>
    public string ExecuteHeavy { get; set; } = string.Empty;

    public PhaseBindings Clone() => new()
    {
        Plan = Plan,
        Review = Review,
        ExecuteLight = ExecuteLight,
        ExecuteHeavy = ExecuteHeavy
    };
}

/// <summary>
/// Persisted app settings. The team-of-models schema (Providers / Workers / Bindings) is authoritative;
/// the legacy single-endpoint + Anthropic-reasoner fields are read once to migrate an old file, then inert.
/// </summary>
internal sealed class AppSettings
{
    /// <summary>The newest settings.json schema this build writes. See <see cref="SchemaVersion"/>.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// settings.json schema version. Files written before 2026-09-06 have no such field and read as 1,
    /// where an EMPTY worker tool list meant "every tool" — the inverted permission the code review
    /// flagged. In version 2 an empty list means "no tools" and full access is spelled "*", so a v1
    /// file's empty lists are rewritten to ["*"] on load: that preserves what those workers were
    /// actually able to do instead of silently disarming them.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    // ── Team-of-models schema (Docs/MODELS.md) ────────────────────────────────
    public List<ProviderConfig> Providers { get; set; } = new();
    public List<WorkerConfig> Workers { get; set; } = new();
    public PhaseBindings Bindings { get; set; } = new();

    public string GlobalInstructions { get; set; } = string.Empty;

    // Ollama context window (options.num_ctx). Null = inherit whatever the model was loaded with.
    public int? NumCtx { get; set; }

    // Send think:false to the local model so a reasoning model (qwen3, ...) answers directly instead of
    // burning a whole turn in <think> with empty content. On by default; only OllamaNative honors it.
    public bool DisableThinking { get; set; } = true;

    // Execute a tool call the model only DESCRIBED in its reply (a ```json block) instead of invoking it.
    // Off by default and deliberately so: a parser cannot tell an intended call from a quoted example,
    // which means anything that can put text in front of the model can put an action in front of the
    // engine. Turn it on only for a weak local model that cannot emit structured tool calls at all.
    public bool AllowImplicitToolCalls { get; set; }

    // When a step runs no commands and only writes text, review the TEXT instead of the (empty)
    // execution evidence. Without this a configured reviewer passes anything such a step produces:
    // there is no exit code in a document, so the execution question has no answer to give. Costs one
    // reviewer call on the written content, which is why it is a switch — but it is on by default,
    // because the alternative is a gate that silently checks nothing for every writing task.
    public bool ReviewContent { get; set; } = true;

    // How many times a rejected step may be redone before the run gives up. 1 means two tries in
    // total, which is what the engine did when this number was hard-coded. It was worth exposing
    // because it is the dial between "the reviewer's feedback gets used" and "a weak model burns the
    // budget arguing with a strong one": in a real run one step needed exactly two attempts and
    // passed, while another used both and was still wrong. Clamped to 0..5 by the orchestrator.
    public int ReviewRetries { get; set; } = 1;

    // Put a rejected step's files back to how they were before it ran. Without this the gate stops
    // only the REPORT: the run says Failed while the rejected document stays in the workspace, which
    // is the version someone is most likely to open next. A file changed since the step wrote it is
    // left alone and named in the log — reverting over somebody's edit would be the very thing this
    // is meant to prevent. Turn it off to inspect what a rejected step actually produced.
    public bool RevertRejectedSteps { get; set; } = true;

    // Ask workers to read a file back after writing it, to catch a weak local model fabricating content.
    // Costs an extra LLM round-trip per write — worth turning off when running strong models. On by default.
    public bool VerifyWrites { get; set; } = true;

    // How many independent plan steps may run at once. 1 = the original behaviour: one step at a time on
    // one shared conversation. Above 1 each concurrent step gets its own forked conversation, seeded with
    // a digest of what earlier steps concluded. Only pays off when steps route to different providers —
    // two steps on one Ollama still queue on the GPU.
    public int MaxParallelSteps { get; set; } = 1;

    // What the main window's close button does. True - the default - hides it to the tray, where a
    // run it started keeps going; false makes closing the window quit the program, asking first if
    // there is work in flight. Ignored when the desktop has no tray: there is nowhere to hide.
    public bool CloseToTray { get; set; } = true;

    // ── Legacy fields (migration source only; superseded by the schema above) ──
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "qwen2.5-coder";
    public bool MultiAgent { get; set; }
    public string AnthropicApiKey { get; set; } = string.Empty;
    public string AnthropicApiKeyProtected { get; set; } = string.Empty;
    public string ReasonerModel { get; set; } = "claude-3-5-sonnet-latest";
    public string AnthropicWorkspaceId { get; set; } = string.Empty;

    // Main window placement (restored on start).
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string SettingsFile()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Enactive", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var file = SettingsFile();
            if (File.Exists(file))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), JsonOptions);
                if (loaded is not null)
                {
                    // Decrypt per-provider keys back into memory (plaintext field is not serialized).
                    foreach (var p in loaded.Providers)
                        if (!string.IsNullOrEmpty(p.ApiKeyProtected))
                            p.ApiKey = Secret.Unprotect(p.ApiKeyProtected);

                    // Legacy key: encrypted form wins; a legacy plaintext value is kept for migration.
                    if (!string.IsNullOrEmpty(loaded.AnthropicApiKeyProtected))
                        loaded.AnthropicApiKey = Secret.Unprotect(loaded.AnthropicApiKeyProtected);

                    loaded.MigrateIfNeeded();
                    return loaded;
                }
            }
        }
        catch { /* ignore */ }

        var seeded = SeedFromEnvironment();
        seeded.MigrateIfNeeded();
        return seeded;
    }

    public void Save()
    {
        try
        {
            var file = SettingsFile();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            // Encrypt every provider key; plaintext is [JsonIgnore] so it never reaches disk.
            foreach (var p in Providers)
                p.ApiKeyProtected = Secret.Protect(p.ApiKey);

            // Legacy key: persist only the encrypted form, blanking the plaintext during serialization.
            AnthropicApiKeyProtected = Secret.Protect(AnthropicApiKey);
            var legacyPlain = AnthropicApiKey;
            AnthropicApiKey = string.Empty;
            try
            {
                File.WriteAllText(file, JsonSerializer.Serialize(this, JsonOptions));
            }
            finally
            {
                AnthropicApiKey = legacyPlain;
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Brings an older settings.json up to <see cref="CurrentSchemaVersion"/>. Unlike the team-schema
    /// migration below, this runs on EVERY load, because it must also reach a file that already has
    /// providers. It is written to be idempotent.
    /// </summary>
    private void MigrateSchemaVersion()
    {
        if (SchemaVersion < 2)
        {
            // v1 semantics: an empty tool list meant "every tool". Spell that out as "*" so the worker
            // keeps the access it had, while the new, honest meaning of [] applies from here on.
            foreach (var w in Workers.Where(w => w.Tools.Count == 0))
                w.Tools.Add("*");
        }

        SchemaVersion = CurrentSchemaVersion;
    }

    /// <summary>
    /// First load of a file that predates the team schema: synthesize Providers / Workers / Bindings from the
    /// legacy fields. Runs only while <see cref="Providers"/> is empty, so it never clobbers a migrated file.
    /// </summary>
    private void MigrateIfNeeded()
    {
        MigrateSchemaVersion();

        if (Providers.Count > 0)
            return;

        Providers.Add(new ProviderConfig
        {
            Id = "ollama",
            DisplayName = "Ollama (local)",
            Kind = ProviderKind.OllamaNative,
            BaseUrl = BaseUrl,
            Models = new List<string> { Model }
        });

        var hasAnthropic = !string.IsNullOrWhiteSpace(AnthropicApiKey);
        if (hasAnthropic)
        {
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(AnthropicWorkspaceId))
                headers["anthropic-workspace-id"] = AnthropicWorkspaceId;

            Providers.Add(new ProviderConfig
            {
                Id = "anthropic",
                DisplayName = "Anthropic",
                Kind = ProviderKind.Anthropic,
                BaseUrl = "https://api.anthropic.com",
                ApiKey = AnthropicApiKey,
                Headers = headers,
                Models = new List<string> { ReasonerModel }
            });
        }

        if (Workers.Count == 0)
            foreach (var w in DefaultWorkers.Seed(new ModelRef("ollama", Model)))
                Workers.Add(new WorkerConfig
                {
                    Id = w.Id,
                    Role = w.Role,
                    Instructions = w.Instructions,
                    Tools = w.ToolAllowlist.ToList(),
                    Level = w.DefaultLevel,
                    Model = FormatRef(w.ModelPolicy.Preferred),
                    Fallback = w.ModelPolicy.Fallback is { } f ? FormatRef(f) : null
                });

        // MultiAgent on + a key present == plan & review on the Anthropic reasoner. Off == empty (single-agent).
        if (MultiAgent && hasAnthropic)
        {
            Bindings.Plan = $"anthropic/{ReasonerModel}";
            Bindings.Review = $"anthropic/{ReasonerModel}";
        }
    }

    /// <summary>Deep copy — so an editor can work on a throwaway copy and discard it on Cancel.</summary>
    public AppSettings Clone() => new()
    {
        // Must be copied: a clone that fell back to 1 would be saved as a v1 file, and the next load
        // would re-run the migration and hand "*" back to a worker the user had just emptied.
        SchemaVersion = SchemaVersion,
        GlobalInstructions = GlobalInstructions,
        NumCtx = NumCtx,
        DisableThinking = DisableThinking,
        AllowImplicitToolCalls = AllowImplicitToolCalls,
        ReviewContent = ReviewContent,
        ReviewRetries = ReviewRetries,
        RevertRejectedSteps = RevertRejectedSteps,
        VerifyWrites = VerifyWrites,
        MaxParallelSteps = MaxParallelSteps,
        CloseToTray = CloseToTray,
        BaseUrl = BaseUrl,
        Model = Model,
        MultiAgent = MultiAgent,
        AnthropicApiKey = AnthropicApiKey,
        AnthropicApiKeyProtected = AnthropicApiKeyProtected,
        ReasonerModel = ReasonerModel,
        AnthropicWorkspaceId = AnthropicWorkspaceId,
        WindowX = WindowX,
        WindowY = WindowY,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        Bindings = Bindings.Clone(),
        Providers = Providers.Select(x => x.Clone()).ToList(),
        Workers = Workers.Select(x => x.Clone()).ToList()
    };

    /// <summary>
    /// Everything wrong with this configuration, in plain language, or empty when it is sound.
    ///
    /// Save used to write settings.json FIRST and build the runtime objects afterwards, where
    /// <c>ToDictionary</c> rejects duplicate ids. The exception took down the settings window — and
    /// then the next launch read the same file, hit the same exception in the MainWindow
    /// constructor, and the app would not start at all. Validating before writing means a
    /// configuration that cannot be built is never persisted. Ids are compared case-insensitively
    /// because the dictionaries are: "foo" and "Foo" are one id here.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        foreach (var provider in Providers)
            if (string.IsNullOrWhiteSpace(provider.Id))
                problems.Add("A provider has no id.");

        foreach (var duplicate in Providers
                     .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                     .GroupBy(p => p.Id.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
            problems.Add($"Two providers share the id \"{duplicate.Key}\" (ids ignore case).");

        foreach (var worker in Workers)
            if (string.IsNullOrWhiteSpace(worker.Id))
                problems.Add("A worker has no id.");

        foreach (var duplicate in Workers
                     .Where(w => !string.IsNullOrWhiteSpace(w.Id))
                     .GroupBy(w => w.Id.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
            problems.Add($"Two workers share the id \"{duplicate.Key}\" (ids ignore case).");

        // A model bound to a provider that is not configured cannot run; better to say so here than
        // to fail on the first request.
        foreach (var (label, reference) in new[]
                 {
                     ("Plan", Bindings.Plan),
                     ("Review", Bindings.Review),
                     ("Execute · light", Bindings.ExecuteLight),
                     ("Execute · heavy", Bindings.ExecuteHeavy)
                 })
        {
            if (ParseRef(reference) is { } model
                && !Providers.Any(p => string.Equals(p.Id, model.ProviderId, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"The {label} model uses provider \"{model.ProviderId}\", which is not configured.");
            }
        }

        foreach (var worker in Workers)
        {
            if (ParseRef(worker.Model) is { } model
                && !Providers.Any(p => string.Equals(p.Id, model.ProviderId, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"Worker \"{worker.Id}\" uses provider \"{model.ProviderId}\", which is not configured.");
            }
        }

        return problems;
    }

    /// <summary>All configured models as "providerId/model" strings, for model pickers.</summary>
    public IReadOnlyList<string> ModelCatalog()
    {
        var list = new List<string>();
        foreach (var p in Providers)
            foreach (var m in p.Models)
                if (!string.IsNullOrWhiteSpace(m))
                    list.Add($"{p.Id}/{m}");
        return list;
    }

    /// <summary>Finds a provider by id (case-insensitive), creating and appending it when absent.</summary>
    public ProviderConfig EnsureProvider(string id, string displayName, ProviderKind kind)
    {
        var existing = Providers.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;
        var created = new ProviderConfig { Id = id, DisplayName = displayName, Kind = kind };
        Providers.Add(created);
        return created;
    }

    /// <summary>"providerId/model" for a <see cref="ModelRef"/>.</summary>
    public static string FormatRef(ModelRef r) => $"{r.ProviderId}/{r.Model}";

    /// <summary>Parses "providerId/model" into a <see cref="ModelRef"/>; null/blank or malformed returns null.</summary>
    public static ModelRef? ParseRef(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        var idx = s.IndexOf('/');
        if (idx <= 0 || idx >= s.Length - 1)
            return null;
        return new ModelRef(s[..idx], s[(idx + 1)..]);
    }

    private static AppSettings SeedFromEnvironment() => new()
    {
        BaseUrl = Environment.GetEnvironmentVariable("ENACTIVE_OLLAMA_URL") ?? "http://localhost:11434/v1",
        Model = Environment.GetEnvironmentVariable("ENACTIVE_MODEL") ?? "qwen2.5-coder",
        GlobalInstructions = string.Empty
    };
}
