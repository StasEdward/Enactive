namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Settings;
using Xunit;

/// <summary>
/// The settings file, read by the code that now serves both hosts.
///
/// <para>These could not exist before. <c>AppSettings</c> was internal to <c>Enactive.App.Ui</c>, a
/// WinExe nothing references, and <c>Load</c> was hard-wired to <c>%APPDATA%</c> — so the migrations,
/// the repairs and the decryption had never been checked by anything except somebody opening the
/// app. The console could not read the file at all, which is why it grew a second configuration and
/// why the first scheduled runs went out on a provider nobody had chosen.</para>
///
/// <para>The first test here is the one the move had to pass: a file written by the shipping build
/// loads with the same meaning afterwards. A refactor that quietly resets somebody's providers is
/// the defect this work is about, inflicted on purpose.</para>
/// </summary>
public sealed class SettingsLoadTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "enactive-settings", Guid.NewGuid().ToString("N"));

    public SettingsLoadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder */ }
    }

    private string Write(string json)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>
    /// A settings.json in the shape this build writes today — the same shape as the one on the
    /// machine where the scheduler was first run, with its two providers and its phase bindings.
    /// No key: what a DPAPI blob decrypts to is the machine's business and not a test's.
    /// </summary>
    private const string AsShipped = """
    {
      "SchemaVersion": 5,
      "KeepRuns": 0,
      "Providers": [
        {
          "Id": "ollama",
          "DisplayName": "Ollama (local)",
          "Kind": "OllamaNative",
          "BaseUrl": "http://localhost:11434/v1",
          "ApiKeyProtected": "",
          "Headers": {},
          "Models": [ "gemma4-12b:latest", "gemma4:31b-cloud" ],
          "MaxTokens": null
        },
        {
          "Id": "Antropic",
          "DisplayName": "Anthropic",
          "Kind": "Anthropic",
          "BaseUrl": "https://api.anthropic.com",
          "ApiKeyProtected": "",
          "Headers": {},
          "Models": [ "claude-sonnet-4-6" ],
          "MaxTokens": 8192
        }
      ],
      "Workers": [
        {
          "Id": "developer",
          "Role": "Developer",
          "Instructions": "do the work",
          "Tools": [ "*" ],
          "Level": "Execute",
          "Model": "ollama/gemma4:31b-cloud",
          "Fallback": null
        }
      ],
      "Bindings": {
        "Plan": "Antropic/claude-sonnet-4-6",
        "Review": "Antropic/claude-sonnet-4-6",
        "ExecuteLight": "ollama/gemma4:31b-cloud",
        "ExecuteHeavy": ""
      },
      "GlobalInstructions": "",
      "NumCtx": 131072,
      "DisableThinking": true,
      "AllowImplicitToolCalls": false,
      "ReviewContent": true,
      "CheckSoundness": true,
      "ReviewRetries": 1,
      "ShellCommands": "Off",
      "CloseToTray": true
    }
    """;

    // ── the move must not change what a file means ──────────────────────────

    [Fact]
    public void A_file_written_by_the_shipping_build_still_loads()
    {
        var settings = AppSettings.Load(Write(AsShipped));

        Assert.Equal(5, settings.SchemaVersion);
        Assert.Empty(settings.LoadProblems);
        Assert.Equal(2, settings.Providers.Count);
    }

    /// <summary>
    /// The provider KIND in particular. It is what the scheduled runs got wrong — they used
    /// OpenAiCompatible against an OllamaNative endpoint — so a load that dropped it back to the
    /// enum's default would reproduce the defect through the fix for it.
    /// </summary>
    [Fact]
    public void The_provider_kind_survives()
    {
        var settings = AppSettings.Load(Write(AsShipped));

        Assert.Equal(ProviderKind.OllamaNative, settings.Providers[0].Kind);
        Assert.Equal(ProviderKind.Anthropic, settings.Providers[1].Kind);
    }

    [Fact]
    public void The_phase_bindings_survive()
    {
        var settings = AppSettings.Load(Write(AsShipped));

        Assert.Equal("Antropic/claude-sonnet-4-6", settings.Bindings.Plan);
        Assert.Equal("Antropic/claude-sonnet-4-6", settings.Bindings.Review);
        Assert.Equal("ollama/gemma4:31b-cloud", settings.Bindings.ExecuteLight);
        Assert.Equal("", settings.Bindings.ExecuteHeavy);
    }

    [Fact]
    public void The_engine_switches_survive()
    {
        var settings = AppSettings.Load(Write(AsShipped));

        Assert.Equal(131072, settings.NumCtx);
        Assert.True(settings.DisableThinking);
        Assert.False(settings.AllowImplicitToolCalls);
        Assert.True(settings.ReviewContent);
        Assert.True(settings.CheckSoundness);
        Assert.Equal(1, settings.ReviewRetries);
    }

    /// <summary>
    /// Including the shell policy, which is the one that decides whether a run may hand a command
    /// line to the operating system at all.
    /// </summary>
    [Fact]
    public void The_shell_policy_survives()
    {
        Assert.Equal(ShellCommandPolicy.Off, AppSettings.Load(Write(AsShipped)).ShellCommands);
    }

    [Fact]
    public void The_worker_and_its_model_survive()
    {
        var worker = Assert.Single(AppSettings.Load(Write(AsShipped)).Workers);

        Assert.Equal("Developer", worker.Role);
        Assert.Equal("ollama/gemma4:31b-cloud", worker.Model);
        Assert.Equal(PermissionLevel.Execute, worker.Level);
        Assert.Equal(new[] { "*" }, worker.Tools);
    }

    // ── a file it cannot read ───────────────────────────────────────────────

    /// <summary>
    /// Defaults are the right thing to RUN on — refusing to start over a damaged file leaves
    /// nowhere to fix it from — but they must not be silent. A defaulted configuration and a chosen
    /// one look identical from the outside, and the person's own file is still sitting on disk.
    /// </summary>
    [Fact]
    public void A_file_that_cannot_be_parsed_is_reported_rather_than_swallowed()
    {
        var settings = AppSettings.Load(Write("{ this is not json"));

        Assert.NotEmpty(settings.LoadProblems);
        Assert.Contains(settings.LoadProblems, p => p.Contains("could not be read"));
    }

    /// <summary>And it says the file was left alone, because the next question is "did I lose it".</summary>
    [Fact]
    public void It_says_the_file_was_not_changed()
    {
        var settings = AppSettings.Load(Write("{ this is not json"));

        Assert.Contains(settings.LoadProblems, p => p.Contains("NOT been changed"));
    }

    [Fact]
    public void A_missing_file_is_not_a_problem_to_report()
    {
        var settings = AppSettings.Load(Path.Combine(_dir, "nothing-here.json"));

        Assert.Empty(settings.LoadProblems);
    }

    // ── the migration that is still load-bearing ────────────────────────────

    /// <summary>
    /// In schema 1 an EMPTY tool list meant "every tool" — an inverted permission. Version 2 made an
    /// empty list mean none and spelled full access "*", so a v1 file's empty lists are rewritten
    /// rather than read literally: reading them as they stand would silently disarm every worker in
    /// somebody's file, which is a different wrong answer from the one being fixed.
    /// </summary>
    [Fact]
    public void A_version_1_workers_empty_tool_list_still_means_every_tool()
    {
        var settings = AppSettings.Load(Write("""
        {
          "Workers": [
            { "Id": "developer", "Role": "Developer", "Tools": [], "Level": "Execute", "Model": "ollama/x" }
          ]
        }
        """));

        Assert.Equal(new[] { "*" }, Assert.Single(settings.Workers).Tools);
    }

    // ── the model nobody chose ──────────────────────────────────────────────
    //
    // A URL has a right default: Ollama is listening on it or it is not, and being wrong costs a
    // connection error naming the address. A MODEL NAME has none — which models exist is a fact
    // about the machine. Until 2026-09-11 this file decided it: a fresh install was configured for
    // "qwen2.5-coder", and the first scheduled runs on a machine holding only gemma4 died on
    // `model 'qwen2.5-coder' not found`. Nobody had chosen either the model or the failure.

    /// <summary>
    /// A machine with no settings file has a provider — the endpoint is a good guess — and NO model,
    /// because that is not a thing this build can know.
    /// </summary>
    [Fact]
    public void A_machine_with_no_settings_file_has_no_model()
    {
        var settings = AppSettings.Load(Path.Combine(_dir, "nothing-here.json"));

        var provider = Assert.Single(settings.Providers);
        Assert.Equal("ollama", provider.Id);
        Assert.Empty(provider.Models);
    }

    /// <summary>
    /// The other half, and the one that must not regress: a legacy file that NAMES a model is a
    /// person's choice, and it is migrated exactly as before. Refusing to guess is not the same as
    /// throwing away what somebody already picked.
    /// </summary>
    [Fact]
    public void A_legacy_file_that_names_a_model_still_migrates_it()
    {
        var settings = AppSettings.Load(Write("""
        {
          "BaseUrl": "http://localhost:11434/v1",
          "Model": "gemma4:31b-cloud"
        }
        """));

        var provider = Assert.Single(settings.Providers);
        Assert.Equal(new[] { "gemma4:31b-cloud" }, provider.Models);
    }

    /// <summary>ENACTIVE_MODEL is still honoured on a machine with no file — it is somebody saying
    /// which model to use, which is exactly what was missing.</summary>
    [Fact]
    public void The_environment_can_still_name_the_model()
    {
        Environment.SetEnvironmentVariable("ENACTIVE_MODEL", "llama3.3:70b");
        try
        {
            var settings = AppSettings.Load(Path.Combine(_dir, "nothing-here.json"));

            Assert.Equal(new[] { "llama3.3:70b" }, Assert.Single(settings.Providers).Models);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ENACTIVE_MODEL", null);
        }
    }

    /// <summary>
    /// A legacy Anthropic key with no reasoner model named gets the provider and no model, for the
    /// same reason: the key says the account exists, not which model it should use.
    /// </summary>
    [Fact]
    public void A_legacy_anthropic_key_without_a_model_names_none()
    {
        var settings = AppSettings.Load(Write("""
        {
          "BaseUrl": "http://localhost:11434/v1",
          "Model": "gemma4:31b-cloud",
          "AnthropicApiKey": "sk-not-a-real-key"
        }
        """));

        Assert.Empty(settings.Providers.Single(p => p.Id == "anthropic").Models);
    }
}
