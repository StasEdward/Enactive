namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Tasks;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using Enactive.Settings;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// What a settings file turns into, for every host.
///
/// <para>These are about one defect, in the form it actually took. On 2026-09-10 two scheduled runs
/// failed with <c>model 'qwen2.5-coder' not found</c> on a machine configured for
/// <c>gemma4:31b-cloud</c>. Nobody had chosen either the model or the provider kind the run used:
/// the window composed its engine in <c>MainWindow.axaml.cs</c> and the console composed its own,
/// and the two had drifted — different kind, different model, no phase bindings at all, so planning
/// bound to Anthropic ran on a local model without anyone being told.</para>
///
/// <para>So the assertions here are deliberately about the FILE, not about the composition's
/// internal consistency: given this settings.json, planning runs on what this settings.json names.
/// A composition that is self-consistent and disagrees with the person's file is the bug.</para>
/// </summary>
public sealed class EngineCompositionTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "enactive-composition", Guid.NewGuid().ToString("N"));

    public EngineCompositionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>
    /// The machine the scheduler first ran on: local Ollama plus Anthropic, planning and review
    /// bound to Anthropic, the worker on the local model, shells off.
    /// </summary>
    private const string AsConfigured = """
    {
      "SchemaVersion": 5,
      "Providers": [
        {
          "Id": "ollama",
          "DisplayName": "Ollama (local)",
          "Kind": "OllamaNative",
          "BaseUrl": "http://localhost:11434/v1",
          "Models": [ "gemma4-12b:latest", "gemma4:31b-cloud" ]
        },
        {
          "Id": "Antropic",
          "DisplayName": "Anthropic",
          "Kind": "Anthropic",
          "BaseUrl": "https://api.anthropic.com",
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
          "Model": "ollama/gemma4:31b-cloud"
        }
      ],
      "Bindings": {
        "Plan": "Antropic/claude-sonnet-4-6",
        "Review": "Antropic/claude-sonnet-4-6",
        "ExecuteLight": "ollama/gemma4-12b:latest",
        "ExecuteHeavy": ""
      },
      "ShellCommands": "Off"
    }
    """;

    private AppSettings Configured(string json = AsConfigured)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);
        return AppSettings.Load(path);
    }

    private static Worker TheWorker(AppSettings settings)
        => EngineComposition.Workers(settings).Default;

    // ── the phase a scheduled run got wrong ─────────────────────────────────

    /// <summary>
    /// The plan's own differential. A run composed from this file plans on Anthropic, because that
    /// is what the file says. The failing scheduled runs planned on the local model — not because
    /// anything overrode the binding, but because the console built a router that had never been
    /// given one.
    /// </summary>
    [Fact]
    public void The_plan_phase_resolves_to_the_provider_the_file_names()
    {
        var settings = Configured();
        var router = EngineComposition.Router(settings, new ModelResolver());

        var plan = router.Resolve(ModelPurpose.Plan, TheWorker(settings));

        Assert.NotNull(plan);
        Assert.Equal("Antropic", plan!.ProviderId);
        Assert.Equal("claude-sonnet-4-6", plan.Model);
    }

    [Fact]
    public void The_review_phase_resolves_to_the_provider_the_file_names()
    {
        var settings = Configured();
        var router = EngineComposition.Router(settings, new ModelResolver());

        var review = router.Resolve(ModelPurpose.Review, TheWorker(settings));

        Assert.Equal(new ModelRef("Antropic", "claude-sonnet-4-6"), review);
    }

    /// <summary>
    /// Execute is the phase that does NOT come from a binding — it is the worker's own model. Worth
    /// pinning, because a composition that routed everything through the Plan binding would pass
    /// the test above and still run the work on the wrong model.
    /// </summary>
    [Fact]
    public void Execute_resolves_to_the_workers_own_model()
    {
        var settings = Configured();
        var router = EngineComposition.Router(settings, new ModelResolver());

        var execute = router.Resolve(ModelPurpose.Execute, TheWorker(settings));

        Assert.Equal(new ModelRef("ollama", "gemma4:31b-cloud"), execute);
    }

    [Fact]
    public void A_trivial_step_goes_to_the_light_model_the_file_names()
    {
        var settings = Configured();
        var router = EngineComposition.Router(settings, new ModelResolver());

        var light = router.ResolveExecute(TheWorker(settings), StepComplexity.Trivial);

        Assert.Equal(new ModelRef("ollama", "gemma4-12b:latest"), light);
    }

    /// <summary>
    /// An empty binding is "not configured", not a model named "". ExecuteHeavy is empty in this
    /// file, so a complex step falls back to the worker's model rather than to nothing.
    /// </summary>
    [Fact]
    public void An_empty_binding_leaves_the_phase_on_the_workers_model()
    {
        var settings = Configured();
        var router = EngineComposition.Router(settings, new ModelResolver());

        var heavy = router.ResolveExecute(TheWorker(settings), StepComplexity.Complex);

        Assert.Equal(new ModelRef("ollama", "gemma4:31b-cloud"), heavy);
    }

    // ── the endpoint ────────────────────────────────────────────────────────

    /// <summary>
    /// The kind, carried through to the descriptor the provider factory is built from. This is the
    /// literal failure: an <c>OllamaNative</c> endpoint addressed as <c>OpenAiCompatible</c>.
    /// </summary>
    [Fact]
    public void The_descriptors_carry_the_provider_kind_from_the_file()
    {
        var descriptors = EngineComposition.Descriptors(Configured());

        Assert.Equal(2, descriptors.Count);
        Assert.Equal(ProviderKind.OllamaNative, descriptors[0].Kind);
        Assert.Equal("http://localhost:11434/v1", descriptors[0].BaseUrl);
        Assert.Equal(ProviderKind.Anthropic, descriptors[1].Kind);
        Assert.Equal(8192, descriptors[1].MaxTokens);
    }

    /// <summary>
    /// A provider with no display name is shown by its id rather than by a blank — the settings
    /// window allows the field to be empty and something has to name it in a picker.
    /// </summary>
    [Fact]
    public void A_provider_without_a_display_name_is_named_by_its_id()
    {
        var settings = Configured("""
        {
          "SchemaVersion": 5,
          "Providers": [
            { "Id": "ollama", "DisplayName": "", "Kind": "OllamaNative",
              "BaseUrl": "http://localhost:11434/v1", "Models": [ "gemma4:31b-cloud" ] }
          ]
        }
        """);

        Assert.Equal("ollama", EngineComposition.Descriptors(settings)[0].DisplayName);
    }

    // ── the model a worker gets when it names none ──────────────────────────

    /// <summary>
    /// The fallback is a model this machine is configured for, not a name compiled in. The console
    /// had <c>qwen2.5-coder</c> hard-coded; the person's Ollama has never had it installed, which is
    /// the 404 the run died on. Any fallback that ignores the file reproduces that.
    /// </summary>
    [Fact]
    public void A_worker_that_names_no_model_falls_back_to_a_configured_one()
    {
        var settings = Configured("""
        {
          "SchemaVersion": 5,
          "Providers": [
            { "Id": "ollama", "Kind": "OllamaNative", "BaseUrl": "http://localhost:11434/v1",
              "Models": [ "gemma4:31b-cloud", "gemma4-12b:latest" ] }
          ],
          "Workers": [
            { "Id": "developer", "Role": "Developer", "Instructions": "x",
              "Tools": [ "*" ], "Level": "Execute", "Model": "" }
          ]
        }
        """);

        Assert.Equal(
            new ModelRef("ollama", "gemma4:31b-cloud"),
            TheWorker(settings).ModelPolicy.Preferred);
    }

    /// <summary>
    /// A file with no team at all still produces one, on the same configured model. A person who has
    /// never opened the workers page must still be able to run something.
    /// </summary>
    [Fact]
    public void A_file_with_no_workers_still_produces_a_team()
    {
        var settings = Configured("""
        {
          "SchemaVersion": 5,
          "Providers": [
            { "Id": "ollama", "Kind": "OllamaNative", "BaseUrl": "http://localhost:11434/v1",
              "Models": [ "gemma4:31b-cloud" ] }
          ],
          "Workers": []
        }
        """);

        var workers = EngineComposition.Workers(settings);

        Assert.NotEmpty(workers.All);
        Assert.Equal(new ModelRef("ollama", "gemma4:31b-cloud"), workers.Default.ModelPolicy.Preferred);
    }

    // ── the setting that only held in one host ──────────────────────────────

    /// <summary>
    /// Shells off means denied — at the top tier too, where the tier's own AskBefore list is empty
    /// and there is nothing to stop them. The window applied this rule and the console did not, so
    /// "never run commands" held for a run started by hand and not for the same workspace run from a
    /// schedule. A rule enforced in one host is not a setting.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Shells_turned_off_are_denied_at_every_tier(int tier)
    {
        var policy = EngineComposition.PolicyFor(Configured(), tier);

        Assert.Contains("run_command", policy.Deny);
        Assert.Contains("run_powershell", policy.Deny);
    }

    [Fact]
    public void Shells_set_to_ask_are_asked_about_at_the_top_tier()
    {
        var settings = Configured(AsConfigured.Replace("\"ShellCommands\": \"Off\"", "\"ShellCommands\": \"Ask\""));

        var policy = EngineComposition.PolicyFor(settings, 3);

        Assert.Contains("run_command", policy.AskBefore);
        Assert.DoesNotContain("run_command", policy.Deny);
    }

    /// <summary>
    /// Follow is the default and changes nothing: the tier decides alone, and the very policy it
    /// produced comes back untouched. Worth its own test, because a WithShells that always added the
    /// names would make every tier stop at a command line and look like the engine had broken.
    ///
    /// <para>Asserted by reference on purpose. <c>PermissionPolicy</c> is a record over arrays, so
    /// its generated equality compares those arrays by reference — two policies with identical
    /// contents are not equal, and an <c>Assert.Equal</c> here would fail while nothing was wrong.
    /// </para>
    /// </summary>
    [Fact]
    public void Shells_set_to_follow_leave_the_tier_alone()
    {
        var tier = AutonomyTiers.PolicyFor(3);

        Assert.Same(tier, EngineComposition.WithShells(tier, ShellCommandPolicy.Follow));
    }

    [Fact]
    public void Shells_set_to_follow_add_nothing_at_any_tier()
    {
        var settings = Configured(AsConfigured.Replace("\"ShellCommands\": \"Off\"", "\"ShellCommands\": \"Follow\""));

        var top = EngineComposition.PolicyFor(settings, 3);

        Assert.Empty(top.Deny);
        Assert.Empty(top.AskBefore);
    }

    /// <summary>
    /// And the tier itself is the shared one. The window kept a private copy of this mapping; a
    /// second copy is a second thing to forget to change.
    /// </summary>
    [Fact]
    public void The_tier_below_the_shell_rule_is_the_shared_one()
    {
        var settings = Configured(AsConfigured.Replace("\"ShellCommands\": \"Off\"", "\"ShellCommands\": \"Follow\""));

        Assert.Equal(PermissionLevel.Execute, EngineComposition.PolicyFor(settings, 2).Level);
        Assert.Contains("git", EngineComposition.PolicyFor(settings, 2).AskBefore);
    }

    // ── a model nobody chose ────────────────────────────────────────────────
    //
    // Until 2026-09-11 a machine with no settings.json was configured for "qwen2.5-coder", because
    // that string was compiled into AppSettings. It is the name the first scheduled runs died on:
    // the person's Ollama had gemma4 and nothing else, and nobody had ever chosen otherwise. A URL
    // has a right default; a model name does not.

    private const string NoModel = """
    {
      "SchemaVersion": 5,
      "Providers": [
        { "Id": "ollama", "Kind": "OllamaNative",
          "BaseUrl": "http://localhost:11434/v1", "Models": [] }
      ]
    }
    """;

    [Fact]
    public void A_provider_with_no_models_has_no_fallback_to_offer()
    {
        Assert.Null(EngineComposition.FallbackModel(Configured(NoModel)));
    }

    [Fact]
    public void Settings_that_name_no_model_say_what_is_missing()
    {
        var missing = EngineComposition.Missing(Configured(NoModel));

        Assert.Contains(missing, m => m.Contains("No model is chosen"));
    }

    /// <summary>
    /// And it names the SETTINGS as the place to fix it. A message that says only "no model" leaves
    /// a person on a machine that will not run with nowhere to go.
    /// </summary>
    [Fact]
    public void The_message_says_where_to_fix_it()
    {
        Assert.Contains("Settings", Assert.Single(EngineComposition.Missing(Configured(NoModel))));
    }

    /// <summary>
    /// No provider at all is its own message. One route to it is <c>new AppSettings()</c>, which
    /// the main window uses as its fallback when a file cannot be applied; that path skips the
    /// migration and really does have nothing.
    /// </summary>
    [Fact]
    public void Settings_with_no_provider_at_all_say_so_separately()
    {
        var missing = EngineComposition.Missing(new AppSettings());

        Assert.Contains(missing, m => m.Contains("No provider"));
    }

    /// <summary>
    /// The other route is a file. This test used to say the opposite - that an empty provider list
    /// in a versioned file came back as the Ollama endpoint - and what it pinned was the defect:
    /// somebody who removed every provider and saved got one back on the next start. A file that
    /// states its version is read as it stands, and says the provider is what is missing.
    /// </summary>
    [Fact]
    public void An_empty_provider_list_in_a_versioned_file_stays_empty_and_says_so()
    {
        var settings = Configured("""{ "SchemaVersion": 5, "Providers": [] }""");

        Assert.Empty(settings.Providers);
        Assert.Contains(EngineComposition.Missing(settings), m => m.Contains("No provider"));
    }

    /// <summary>A file from before the team schema - no version - still gets the endpoint, with no
    /// model, so it reports the model and not the provider. Two different states, two different
    /// sentences.</summary>
    [Fact]
    public void A_file_with_no_version_and_no_provider_becomes_the_endpoint_with_no_model()
    {
        var settings = Configured("""{ "Providers": [] }""");

        Assert.Single(settings.Providers);
        Assert.Contains(EngineComposition.Missing(settings), m => m.Contains("No model is chosen"));
    }

    /// <summary>
    /// Refused, not substituted. Returning an engine on an invented model is the whole defect: it
    /// looks configured, runs, and fails at the provider with an HTTP status for a reason.
    /// </summary>
    [Fact]
    public void An_engine_cannot_be_built_without_a_model()
    {
        var settings = Configured(NoModel);
        using var http = new HttpClient();
        using var log = new LogHub();

        Assert.Throws<InvalidOperationException>(() => EngineComposition.Build(settings, http, log));
    }

    [Fact]
    public void A_team_cannot_be_built_without_a_model()
    {
        Assert.Throws<InvalidOperationException>(() => EngineComposition.Workers(Configured(NoModel)));
    }

    /// <summary>A configuration that DOES name a model has nothing missing — the check must not
    /// refuse the ordinary case it was added to protect.</summary>
    [Fact]
    public void A_configured_machine_is_missing_nothing()
    {
        Assert.Empty(EngineComposition.Missing(Configured()));
    }

    /// <summary>
    /// The fallback is the first provider that HAS a model, not the first provider. A machine whose
    /// first entry is a half-finished provider still runs on the one that is finished.
    /// </summary>
    [Fact]
    public void An_empty_provider_is_skipped_for_one_that_has_a_model()
    {
        var settings = Configured("""
        {
          "SchemaVersion": 5,
          "Providers": [
            { "Id": "half-done", "Kind": "OpenAiCompatible", "BaseUrl": "http://x", "Models": [] },
            { "Id": "ollama", "Kind": "OllamaNative",
              "BaseUrl": "http://localhost:11434/v1", "Models": [ "gemma4:31b-cloud" ] }
          ]
        }
        """);

        Assert.Equal(new ModelRef("ollama", "gemma4:31b-cloud"), EngineComposition.FallbackModel(settings));
        Assert.Empty(EngineComposition.Missing(settings));
    }

    // ── one list, in the three places that act on it ────────────────────────

    /// <summary>
    /// Turning shells off denies the shell list and NOTHING else — no extra name swept in, none of
    /// the list left out.
    ///
    /// <para>It cannot catch a name missing from <see cref="ShellTools.All"/> itself, and does not
    /// try to: <see cref="ShellTools.IsShell"/> now answers from that same list, so the two agree by
    /// construction and a test of their agreement would pass whatever the list said. The literals
    /// live in <c>Shells_turned_off_are_denied_at_every_tier</c> instead, which is where a name has
    /// to be written down twice for anything to notice it going missing — in the test, not in a
    /// second copy of the rule.</para>
    /// </summary>
    [Fact]
    public void Turning_shells_off_denies_the_shell_list_and_nothing_else()
    {
        var denied = EngineComposition.PolicyFor(Configured(), 3).Deny;

        Assert.Equal(ShellTools.All.OrderBy(n => n), denied.OrderBy(n => n));
    }

    /// <summary>The remote rule and the setting refuse the same list — they are the same list.</summary>
    [Fact]
    public void The_remote_rule_and_the_setting_refuse_the_same_tools()
    {
        var bare = AutonomyTiers.PolicyFor(3);

        Assert.Equal(
            RemotePolicy.ForRemoteRun(bare).Deny.OrderBy(n => n),
            EngineComposition.WithShells(bare, ShellCommandPolicy.Off).Deny.OrderBy(n => n));
    }

    /// <summary>
    /// Applied twice, a shell is named once. A remote run in a workspace whose settings already deny
    /// shells goes through both rules, and a policy listing <c>run_command</c> twice is a policy
    /// somebody will one day read as two different rules.
    /// </summary>
    [Fact]
    public void Denying_shells_twice_names_them_once()
    {
        var once = EngineComposition.WithShells(AutonomyTiers.PolicyFor(3), ShellCommandPolicy.Off);

        Assert.Equal(once.Deny.Count, RemotePolicy.ForRemoteRun(once).Deny.Count);
    }

    // ── all of it at once ───────────────────────────────────────────────────

    /// <summary>
    /// The whole composition from a file on disk, which is the shape a host actually calls. Nothing
    /// here talks to a provider — building an engine must not require the endpoints to be up, or a
    /// scheduled run on a machine with Ollama stopped would fail before it could report why.
    /// </summary>
    [Fact]
    public void The_whole_engine_composes_from_a_file()
    {
        var settings = Configured();
        using var http = new HttpClient();
        using var log = new LogHub();

        var engine = EngineComposition.Build(settings, http, log);

        Assert.Equal("gemma4:31b-cloud", engine.DefaultModel);
        Assert.Equal(
            new ModelRef("Antropic", "claude-sonnet-4-6"),
            engine.Router.Resolve(ModelPurpose.Plan, engine.Workers.Default));
    }
}
