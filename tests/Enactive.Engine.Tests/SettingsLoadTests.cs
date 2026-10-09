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
      "TaskReview": true,
      "ShortReview": true,
      "CheckDerivedFigures": true,
      "ReviewRetries": 1,
      "ShellCommands": "Off",
      "CloseToTray": true
    }
    """;

    // ── the move must not change what a file means ──────────────────────────

    /// <summary>
    /// A team saved before restore_file existed gets it where write_file is - and only there (run 457159,
    /// 2026-10-09: the tool was registered and named by no role, because only the built-in roles had it).
    /// </summary>
    [Fact]
    public void A_saved_team_gets_restore_file_where_it_may_write()
    {
        var settings = AppSettings.Load(Write(AsShipped.Replace("\"SchemaVersion\": 5", "\"SchemaVersion\": 6")
            .Replace("\"Tools\": [ \"*\" ]", "\"Tools\": [ \"write_file\", \"read_file\" ]")));

        Assert.Contains("restore_file", settings.Workers.Single().Tools);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    /// <summary>A team saved before run_tests existed gets it where run_command is (schema 8): a role named by no saved team is never offered.</summary>
    [Fact]
    public void A_saved_team_gets_run_tests_where_it_may_run_commands()
    {
        var settings = AppSettings.Load(Write(AsShipped.Replace("\"SchemaVersion\": 5", "\"SchemaVersion\": 7")
            .Replace("\"Tools\": [ \"*\" ]", "\"Tools\": [ \"run_command\", \"read_file\" ]")));

        Assert.Contains("run_tests", settings.Workers.Single().Tools);
        Assert.Equal(8, settings.SchemaVersion);
    }

    [Fact]
    public void A_saved_role_that_cannot_write_does_not_get_restore_file()
    {
        var settings = AppSettings.Load(Write(AsShipped.Replace("\"SchemaVersion\": 5", "\"SchemaVersion\": 6")
            .Replace("\"Tools\": [ \"*\" ]", "\"Tools\": [ \"read_file\" ]")));

        Assert.DoesNotContain("restore_file", settings.Workers.Single().Tools);
    }

    [Fact]
    public void A_file_written_by_the_shipping_build_still_loads()
    {
        var settings = AppSettings.Load(Write(AsShipped));

        // Migrated to whatever this build writes: the point of the test is that the file still
        // LOADS and keeps its meaning, and pinning the number here would turn every schema bump
        // into a failure about the bump rather than about the file.
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
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

        // A file from before the switches had their own section: moved into it on load.
        Assert.Equal(131072, settings.Engine.NumCtx);
        Assert.True(settings.Engine.DisableThinking);
        Assert.False(settings.Engine.AllowImplicitToolCalls);
        // ReviewContent, CheckSoundness, TaskReview and ShortReview switched the earlier step review, gone since
        // 2026-09-30, and CheckDerivedFigures a rule of the short one, always on since 2026-10-01: a file that still has
        // them loads, with no problem said (the test above), and they do nothing.
        Assert.Equal(1, settings.Engine.ReviewRetries);
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
    /// which model to use, which is exactly what was missing.
    ///
    /// <para>The <c>finally</c> used to unconditionally CLEAR the variable rather than restore
    /// whatever it held before this test ran (the code review of 2026-09-24
    /// #5): on a machine or CI runner where ENACTIVE_MODEL is set process-wide, this test wiped it for
    /// every reader that ran after it in the same process - and process-wide state is not something a
    /// method boundary can fence in on its own, so it is restored to what it actually was.</para>
    /// </summary>
    [Fact]
    public void The_environment_can_still_name_the_model()
    {
        var original = Environment.GetEnvironmentVariable("ENACTIVE_MODEL");
        Environment.SetEnvironmentVariable("ENACTIVE_MODEL", "llama3.3:70b");
        try
        {
            var settings = AppSettings.Load(Path.Combine(_dir, "nothing-here.json"));

            Assert.Equal(new[] { "llama3.3:70b" }, Assert.Single(settings.Providers).Models);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ENACTIVE_MODEL", original);
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

    // ── a list somebody emptied ─────────────────────────────────────────────
    //
    // The legacy migration used to run whenever the provider list was empty, so "not migrated yet"
    // and "the person removed them all" were one state: every provider was removed, the file was
    // saved, and the next start brought back an Ollama endpoint and - when the workers were gone
    // too - the whole default team. What tells the two apart is the version the FILE carries: a
    // file that states one was written by a build that already had the team schema.

    /// <summary>The report itself: what the settings window does, then a restart.</summary>
    [Fact]
    public void Providers_and_workers_removed_and_saved_stay_removed_after_a_restart()
    {
        var path = Path.Combine(_dir, "settings.json");
        var settings = AppSettings.Load(path);
        Assert.NotEmpty(settings.Providers);
        Assert.NotEmpty(settings.Workers);

        settings.Providers.Clear();
        settings.Workers.Clear();
        Assert.True(settings.Save(path), settings.LastSaveError);

        var again = AppSettings.Load(path);

        Assert.Empty(again.Providers);
        Assert.Empty(again.Workers);
        Assert.Empty(again.LoadProblems);
    }

    /// <summary>And through the copy the settings window really saves: it edits a clone.</summary>
    [Fact]
    public void The_same_through_the_clone_the_settings_window_saves()
    {
        var path = Path.Combine(_dir, "settings.json");
        var edited = AppSettings.Load(path).Clone();

        edited.Providers.Clear();
        edited.Workers.Clear();
        Assert.True(edited.Save(path), edited.LastSaveError);

        var again = AppSettings.Load(path);

        Assert.Empty(again.Providers);
        Assert.Empty(again.Workers);
    }

    /// <summary>
    /// Every version that has ever been written into a file, not only today's: the files already on
    /// disks were saved at 2 to 6, and a rule that began at the current number would leave each of
    /// them reseeded once more.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(AppSettings.CurrentSchemaVersion)]
    public void A_file_that_states_its_version_keeps_an_empty_provider_list(int version)
    {
        var settings = AppSettings.Load(Write(
            $$"""{ "SchemaVersion": {{version}}, "Providers": [], "Workers": [] }"""));

        Assert.Empty(settings.Providers);
        Assert.Empty(settings.Workers);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    /// <summary>
    /// The legacy fields do not bring the migration back. Every saved file still carries them -
    /// they are ordinary properties - so their presence says nothing about whether the file was
    /// migrated, and a rule that looked at them would reseed exactly the file in the report.
    /// </summary>
    [Fact]
    public void Legacy_fields_left_in_a_versioned_file_do_not_bring_a_provider_back()
    {
        var settings = AppSettings.Load(Write($$"""
        {
          "SchemaVersion": {{AppSettings.CurrentSchemaVersion}},
          "Providers": [],
          "Workers": [],
          "BaseUrl": "http://localhost:11434/v1",
          "Model": "gemma4:31b-cloud"
        }
        """));

        Assert.Empty(settings.Providers);
        Assert.Empty(settings.Workers);
    }

    /// <summary>The workers are the person's too: providers removed, team kept, and it is kept as written.</summary>
    [Fact]
    public void Removing_the_providers_leaves_the_workers_as_they_were()
    {
        var settings = AppSettings.Load(Write($$"""
        {
          "SchemaVersion": {{AppSettings.CurrentSchemaVersion}},
          "Providers": [],
          "Workers": [
            { "Id": "writer", "Role": "Writer", "Tools": [], "Level": "Execute", "Model": "gone/x" }
          ]
        }
        """));

        Assert.Empty(settings.Providers);
        Assert.Equal("writer", Assert.Single(settings.Workers).Id);
    }

    /// <summary>
    /// A file from before the team schema states no version and lists no provider, and is migrated
    /// in full as it always was: the endpoint, the Anthropic account, the default team on the model
    /// it named, and plan and review on the reasoner.
    /// </summary>
    [Fact]
    public void A_file_from_before_the_team_schema_is_still_migrated_in_full()
    {
        var settings = AppSettings.Load(Write("""
        {
          "BaseUrl": "http://localhost:11434/v1",
          "Model": "gemma4:31b-cloud",
          "MultiAgent": true,
          "AnthropicApiKey": "sk-not-a-real-key",
          "ReasonerModel": "claude-sonnet-4-6"
        }
        """));

        Assert.Equal(new[] { "ollama", "anthropic" }, settings.Providers.Select(p => p.Id));
        Assert.NotEmpty(settings.Workers);
        Assert.All(settings.Workers, w => Assert.Equal("ollama/gemma4:31b-cloud", w.Model));
        Assert.Equal("anthropic/claude-sonnet-4-6", settings.Bindings.Plan);
        Assert.Equal("anthropic/claude-sonnet-4-6", settings.Bindings.Review);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    /// <summary>A file with providers is not touched by any of this, whatever version it states.</summary>
    [Fact]
    public void A_file_with_providers_gains_none()
    {
        var settings = AppSettings.Load(Write($$"""
        {
          "SchemaVersion": {{AppSettings.CurrentSchemaVersion}},
          "Providers": [ { "Id": "mine", "Kind": "OpenAiCompatible", "BaseUrl": "http://example.test/v1" } ],
          "Workers": []
        }
        """));

        Assert.Equal("mine", Assert.Single(settings.Providers).Id);
        Assert.Empty(settings.Workers);
    }

    /// <summary>A first run - no file - still starts with the endpoint and the default team.</summary>
    [Fact]
    public void A_machine_with_no_settings_file_still_gets_the_endpoint_and_the_default_team()
    {
        var settings = AppSettings.Load(Path.Combine(_dir, "nothing-here.json"));

        Assert.Equal("ollama", Assert.Single(settings.Providers).Id);
        Assert.NotEmpty(settings.Workers);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }
    /// <summary>
    /// Settings that never came from a file - the defaults a host runs on when the file could not be read - still
    /// said "version 1" when saved, which is what a file from before versions says. So the settings a person put
    /// right in that state and saved were read back as a legacy file: an endpoint and the default team were added
    /// to what they had saved, and a worker they had given no tools was given every tool, which is what an empty
    /// list meant at version 1. What is saved is written by this build, in this build's meaning, and says so.
    /// </summary>
    [Fact]
    public void Settings_saved_without_ever_being_loaded_are_read_back_as_they_were_saved()
    {
        var path = Path.Combine(_dir, "settings.json");
        var settings = new AppSettings();
        settings.Providers.Add(new ProviderConfig { Id = "local", DisplayName = "Local", BaseUrl = "http://localhost:8080", Models = ["small"] });
        settings.Workers.Add(new WorkerConfig { Id = "reader", Role = "Reader", Model = "local/small" });   // no tools, on purpose

        Assert.True(settings.Save(path), settings.LastSaveError);
        var again = AppSettings.Load(path);

        Assert.Equal("local", Assert.Single(again.Providers).Id);       // nothing seeded beside it
        Assert.Empty(Assert.Single(again.Workers).Tools);               // and not widened to "*"
        Assert.Equal(AppSettings.CurrentSchemaVersion, again.SchemaVersion);
    }

    [Fact]
    public void A_saved_file_states_the_schema_it_was_written_in()
    {
        var path = Path.Combine(_dir, "settings.json");

        Assert.True(new AppSettings().Save(path));

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(AppSettings.CurrentSchemaVersion, doc.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Empty(AppSettings.Load(path).Providers);                 // empty as saved, not a legacy file to fill in
    }
}
