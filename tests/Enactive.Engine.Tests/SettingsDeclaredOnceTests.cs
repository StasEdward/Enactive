namespace Enactive.Engine.Tests;

using System.Reflection;
using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Secrets;
using Enactive.Settings;
using Enactive.Tools.Mcp;
using Xunit;

/// <summary>
/// What the settings hold reaches the engine, survives a copy and is kept secret - each declared once.
///
/// <para>Until 2026-10-08 an engine switch was five edits (a setting, a copy, an options record, a capture and
/// a constructor parameter) with two sets of defaults that had drifted; a provider's settings were mapped by
/// position across six int? in a row; and each kind of secret was handled by hand in four places, so a password
/// left in the clear was reported for a provider and not for SMTP, and MCP credentials that could not be
/// decrypted were never reported at all.</para>
/// </summary>
public sealed class SettingsDeclaredOnceTests
{
    // ── engine switches ─────────────────────────────────────────────────────
    //
    // A switch is one property of EngineOptions, and the settings hold that very object ("Engine" in settings.json).
    // Nothing maps one to the other, so these ask what is left to go wrong: the file, old and new.

    /// <summary>Every switch, set to something other than its default, comes back from the file as it was set.</summary>
    [Fact]
    public void Every_engine_switch_survives_saving_and_loading()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        var engine = EveryEngineSwitchChanged();
        Assert.True(new AppSettings { Engine = engine }.Save(path));

        Assert.Equal(engine, AppSettings.Load(path).Engine);
    }

    /// <summary>
    /// What a build from before the "Engine" section reads - its top-level switches, by the names it had for them -
    /// is in the saved file too, with the values this build runs on. Written to the section only, the file left such
    /// a build (an older install beside this one, a release rolled back) running on its own defaults without a word.
    /// </summary>
    [Fact]
    public void A_build_from_before_the_engine_section_reads_the_same_switches()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        Assert.True(new AppSettings { Engine = EveryEngineSwitchChanged() }.Save(path));

        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var engine = saved["Engine"]!;
        Assert.All(OlderBuildsSwitches, field =>
        {
            var inEngine = field.InEngine.Aggregate(engine, (node, name) => node[name]!);
            Assert.True(JsonNode.DeepEquals(inEngine, saved[field.TopLevel]), $"{field.TopLevel}: {saved[field.TopLevel]?.ToJsonString() ?? "missing"}, Engine has {inEngine.ToJsonString()}");
        });
    }

    /// <summary>
    /// A build from before the section saves the file without it - it drops what it does not know - and with whatever
    /// a person changed there. Opened here again, nothing is lost: the switches come back from the top level, the
    /// change with them. Before the copy this build found no section and nothing at the top level, and every switch
    /// a person had set was back at its default.
    /// </summary>
    [Fact]
    public void Switches_saved_by_a_build_from_before_the_section_come_back()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        var engine = EveryEngineSwitchChanged();
        Assert.True(new AppSettings { Engine = engine }.Save(path));
        Edit(path, root =>
        {
            // What the older build's save leaves: no "Engine", and two switches it was used to change.
            root.AsObject().Remove("Engine");
            root["MaxParallelSteps"] = 5;
            root["MaxTotalSteps"] = 77;
        });

        Assert.Equal(engine with { MaxParallelSteps = 5, FanOut = engine.FanOut with { MaxTotalSteps = 77 } }, AppSettings.Load(path).Engine);
    }

    /// <summary>A new installation runs on the engine's own defaults: there is no other set.</summary>
    [Fact]
    public void A_new_installation_runs_on_the_engine_s_own_defaults()
        => Assert.Equal(EngineOptions.Default, new AppSettings().Engine);

    /// <summary>
    /// A file written before a switch existed is read without it, and the switch keeps the default written on its
    /// property - no migration, no second default.
    /// </summary>
    [Fact]
    public void A_switch_the_file_does_not_have_keeps_its_default()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        Assert.True(new AppSettings().Save(path));
        Edit(path, root => root["Engine"] = new JsonObject { ["ReviewRetries"] = 3 });

        Assert.Equal(EngineOptions.Default with { ReviewRetries = 3 }, AppSettings.Load(path).Engine);
    }

    /// <summary>
    /// A file from before the switches had their own section held them at the top level - and the plan-growth limits
    /// under names of their own. They are moved into "Engine" on load; the next save writes them there, and copies them
    /// to the top level for a build from before the section.
    /// </summary>
    [Fact]
    public void A_file_from_before_the_engine_section_moves_its_switches_in()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        Assert.True(new AppSettings().Save(path));
        Edit(path, root =>
        {
            root.AsObject().Remove("Engine");
            root["ReviewRetries"] = 3;
            root["DisableThinking"] = false;
            root["NumCtx"] = 4096;
            root["MaxStepsPerExpansion"] = 7;
            root["MaxFanOutDepth"] = 1;
        });

        var loaded = AppSettings.Load(path);

        Assert.Equal(EngineOptions.Default with
        {
            ReviewRetries = 3, DisableThinking = false, NumCtx = 4096,
            FanOut = FanOutLimits.Default with { MaxStepsPerExpansion = 7, MaxDepth = 1 }
        }, loaded.Engine);
        Assert.True(loaded.Save(path));
        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(3, (int)saved["Engine"]!["ReviewRetries"]!);
        Assert.Equal(7, (int)saved["Engine"]!["FanOut"]!["MaxStepsPerExpansion"]!);
        Assert.Equal(3, (int)saved["ReviewRetries"]!);
        Assert.Equal(7, (int)saved["MaxStepsPerExpansion"]!);
    }

    /// <summary>
    /// Only the names the flat layout had are moved. FanOut is a switch of the engine's but never was a top-level field
    /// (its limits were three numbers of their own), so a stray top-level "FanOut" is nothing to move - reading the
    /// engine's property names moved it, and would have moved any switch added later out of a field sharing its name.
    /// </summary>
    [Fact]
    public void A_top_level_field_the_flat_layout_never_had_is_not_moved_in()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        Assert.True(new AppSettings().Save(path));
        Edit(path, root =>
        {
            root.AsObject().Remove("Engine");
            root["FanOut"] = new JsonObject { ["MaxDepth"] = 9 };
        });

        Assert.Equal(FanOutLimits.Default, AppSettings.Load(path).Engine.FanOut);
    }

    /// <summary>Where a file has both, the section is what was saved last - a stray top-level field does not override it.</summary>
    [Fact]
    public void The_engine_section_wins_over_a_stray_top_level_switch()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        Assert.True(new AppSettings { Engine = new() { ReviewRetries = 4 } }.Save(path));
        Edit(path, root => root["ReviewRetries"] = 0);

        Assert.Equal(4, AppSettings.Load(path).Engine.ReviewRetries);
    }

    /// <summary>
    /// The switches a build from before the "Engine" section reads at the top level, and where each is in the section.
    /// Spelled out here rather than read from AppSettings: it is what those builds read, and it does not change.
    /// </summary>
    private static readonly (string TopLevel, string[] InEngine)[] OlderBuildsSwitches =
    [
        .. new[]
        {
            "NumCtx", "GenerationBudgets", "RepairConsultation", "DisableThinking", "AllowImplicitToolCalls", "ReviewRetries",
            "MaxLoadedToolsPerStep", "SuccessRetries", "ProposeChecks", "StepOutputs", "TypedCriteria", "DynamicSteps",
            "ValidateWaves", "ReportBlocked", "SemanticCriteria", "RevertRejectedSteps", "MaxParallelSteps", "EvidenceBudget"
        }.Select(name => (name, new[] { name })),
        ("MaxStepsPerExpansion", ["FanOut", "MaxStepsPerExpansion"]),
        ("MaxTotalSteps", ["FanOut", "MaxTotalSteps"]),
        ("MaxFanOutDepth", ["FanOut", "MaxDepth"]),
    ];

    /// <summary>Every switch set to something other than its default, each to a value of its own.</summary>
    private static EngineOptions EveryEngineSwitchChanged()
    {
        var engine = new EngineOptions { FanOut = new FanOutLimits(91, 92, 93) };
        var n = 0;
        foreach (var option in typeof(EngineOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.Name != nameof(EngineOptions.FanOut)))
            option.SetValue(engine, Different(option.PropertyType, option.GetValue(EngineOptions.Default), ++n));
        return engine;
    }

    private static object? Different(Type type, object? value, int n)
        => type == typeof(bool) ? !(bool)value!
            : type == typeof(int) ? (int)value! + 100 + n
            : type == typeof(int?) ? 1000 + n
            : type == typeof(GenerationBudgets) ? new GenerationBudgets(Action: 2000 + n)
            : type == typeof(RepairConsultation) ? new RepairConsultation(FailedRepairs: n)
            : throw new Xunit.Sdk.XunitException($"No different value is known for {type.Name}; add one here.");

    // ── provider settings ───────────────────────────────────────────────────

    /// <summary>
    /// Every number and switch a provider is configured with arrives in the field of the same name. Each is given
    /// its own value, so two that changed places in the mapping cannot pass.
    /// </summary>
    [Fact]
    public void Every_provider_setting_arrives_in_the_field_of_its_name()
    {
        var provider = new ProviderConfig { Id = "local", BaseUrl = "http://localhost:11434/v1" };
        var copied = new List<PropertyInfo>();
        var n = 0;
        foreach (var field in typeof(ProviderDescriptor).GetProperties())
        {
            if (typeof(ProviderConfig).GetProperty(field.Name) is not { CanWrite: true } setting
                || setting.PropertyType != field.PropertyType
                || field.PropertyType is var t && t != typeof(int) && t != typeof(int?) && t != typeof(bool))
                continue;
            n++;
            setting.SetValue(provider, field.PropertyType == typeof(bool) ? true : 500 + n);
            copied.Add(field);
        }

        var descriptor = Assert.Single(EngineComposition.Descriptors(new AppSettings { Providers = [provider] }));

        Assert.True(copied.Count >= 10, "the provider's numbers and switches are what this is about");
        foreach (var field in copied)
            Assert.Equal(typeof(ProviderConfig).GetProperty(field.Name)!.GetValue(provider), field.GetValue(descriptor));
    }

    // ── copies ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A copy is made by value and only what can be changed in place is copied by hand - so every such member must
    /// be, or the settings editor's copy reaches into the live settings. Each one is checked, by type.
    /// </summary>
    [Fact]
    public void A_copy_shares_nothing_that_can_be_changed_in_place()
    {
        var settings = new AppSettings { Providers = [new ProviderConfig { Id = "local" }] };
        var copy = settings.Clone();

        foreach (var (owner, original, cloned) in new (Type, object, object)[]
                 { (typeof(AppSettings), settings, copy), (typeof(ProviderConfig), settings.Providers[0], copy.Providers[0]) })
            foreach (var property in owner.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.GetIndexParameters().Length == 0 && Mutable(p.PropertyType)))
            {
                var a = property.GetValue(original);
                if (a is not null)
                    Assert.False(ReferenceEquals(a, property.GetValue(cloned)), $"{owner.Name}.{property.Name} is shared by the copy");
            }
    }

    /// <summary>
    /// A type whose instances can be changed after they are made: a collection that is not read-only, or a class
    /// with a setter. A read-only list (IReadOnlyList) cannot be changed through the property, so sharing it is safe.
    /// </summary>
    private static bool Mutable(Type type)
        => type != typeof(string)
           && !type.IsValueType
           && !(type.IsInterface && type.Name.StartsWith("IReadOnly", StringComparison.Ordinal))
           && (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && !type.IsArray
               || type.GetProperties().Any(p => p.SetMethod is { IsPublic: true } set
                   && !set.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit))));

    // ── secrets ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Every encrypted field the settings carry is in the one list loading, saving and the startup report go
    /// through. A new kind of secret added as a "...Protected" field and not to the list fails here.
    /// </summary>
    [Fact]
    public void Every_protected_field_is_one_of_the_settings_secrets()
    {
        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Id = "local" }],
            McpServers = [new McpServerConfig { Id = "files" }]
        };
        var marked = new HashSet<string>();
        Mark(settings, marked, 0);

        Assert.Equal(marked.Order(), settings.Secrets().Select(s => s.Stored()).Order());
    }

    private static void Mark(object node, HashSet<string> marked, int depth)
    {
        if (depth > 4) return;
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetIndexParameters().Length == 0))
        {
            if (property.PropertyType == typeof(string) && property.Name.EndsWith("Protected", StringComparison.Ordinal)
                && property.CanWrite)
            {
                var marker = $"{node.GetType().Name}.{property.Name}#{marked.Count}";
                property.SetValue(node, marker);
                marked.Add(marker);
            }
            else if (property.GetValue(node) is System.Collections.IEnumerable items and not string)
            {
                foreach (var item in items)
                    if (Ours(item))
                        Mark(item!, marked, depth + 1);
            }
            else if (property.GetValue(node) is { } child && Ours(child))
            {
                Mark(child, marked, depth + 1);
            }
        }
    }

    /// <summary>One of this application's own settings objects - not a string, a number or a framework collection.</summary>
    private static bool Ours(object? value)
        => value is not null && value.GetType() is { IsClass: true } type && type != typeof(string)
           && type.Namespace?.StartsWith("Enactive", StringComparison.Ordinal) == true;

    [Fact]
    public void Every_secret_is_encrypted_on_disk_and_read_back_whole()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Id = "cloud", ApiKey = "sk-cloud" }],
            AnthropicApiKey = "sk-legacy",
            McpServers = [new McpServerConfig { Id = "files", Environment = new() { ["TOKEN"] = "mcp-token" } }]
        };
        settings.RemoteAccess.Token = "device-token";
        settings.Smtp.Password = "smtp-password";
        Assert.True(settings.Save(path), settings.LastSaveError);

        var text = File.ReadAllText(path);
        foreach (var secret in new[] { "sk-cloud", "sk-legacy", "device-token", "smtp-password", "mcp-token" })
            Assert.DoesNotContain(secret, text);

        var loaded = AppSettings.Load(path);
        Assert.Empty(loaded.LoadProblems);
        Assert.Equal("sk-cloud", loaded.Providers[0].ApiKey);
        Assert.Equal("sk-legacy", loaded.AnthropicApiKey);
        Assert.Equal("device-token", loaded.RemoteAccess.Token);
        Assert.Equal("smtp-password", loaded.Smtp.Password);
        Assert.Equal("mcp-token", loaded.McpServers[0].Environment["TOKEN"]);
    }

    /// <summary>A password left in the clear was reported for a provider and not for the mail account.</summary>
    [Fact]
    public void An_smtp_password_left_in_the_clear_is_reported()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        Assert.True(new AppSettings().Save(path));
        Edit(path, root => root["Smtp"]!["PasswordProtected"] = "smtp-password");

        var loaded = AppSettings.Load(path);

        Assert.Contains(loaded.LoadProblems, p => p.StartsWith("The SMTP password is stored UNENCRYPTED", StringComparison.Ordinal));
        Assert.Equal("smtp-password", loaded.Smtp.Password);
    }

    /// <summary>
    /// MCP credentials that could not be decrypted turned the server off and said so only in the server list, in
    /// grey. They are reported with everything else at startup, and kept as they were until somebody re-enters them.
    /// </summary>
    [Fact]
    public void Mcp_credentials_that_cannot_be_decrypted_are_reported_and_kept()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        const string unreadable = "dpapi:bm90IHRoaXMgbWFjaGluZQ==";
        Assert.True(new AppSettings { McpServers = [new McpServerConfig { Id = "files", Enabled = true }] }.Save(path));
        Edit(path, root => root["McpServers"]![0]!["SecretsProtected"] = unreadable);

        var loaded = AppSettings.Load(path);

        Assert.True(loaded.McpServers[0].CredentialsUnavailable);
        Assert.Contains(loaded.LoadProblems, p => p.StartsWith("The credentials for MCP server 'files' cannot be decrypted", StringComparison.Ordinal));
        Assert.True(loaded.Save(path), loaded.LastSaveError);
        Assert.Equal(unreadable, AppSettings.Load(path).McpServers[0].SecretsProtected);
    }

    [Fact]
    public void Mcp_credentials_left_in_the_clear_are_reported_and_still_used()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        Assert.True(new AppSettings { McpServers = [new McpServerConfig { Id = "files" }] }.Save(path));
        Edit(path, root => root["McpServers"]![0]!["SecretsProtected"] = """{"Environment":{"TOKEN":"mcp-token"},"Headers":{}}""");

        var loaded = AppSettings.Load(path);

        Assert.Contains(loaded.LoadProblems, p => p.StartsWith("The credentials for MCP server 'files' are stored UNENCRYPTED", StringComparison.Ordinal));
        Assert.Equal("mcp-token", loaded.McpServers[0].Environment["TOKEN"]);
        Assert.False(loaded.McpServers[0].CredentialsUnavailable);
    }

    [Fact]
    public void A_provider_key_that_cannot_be_decrypted_is_reported_and_kept()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        const string unreadable = "dpapi:bm90IHRoaXMgbWFjaGluZQ==";
        Assert.True(new AppSettings { Providers = [new ProviderConfig { Id = "cloud" }] }.Save(path));
        Edit(path, root => root["Providers"]![0]!["ApiKeyProtected"] = unreadable);

        var loaded = AppSettings.Load(path);

        Assert.Contains(loaded.LoadProblems, p => p.StartsWith("The API key for provider 'cloud' cannot be decrypted", StringComparison.Ordinal));
        Assert.True(loaded.Save(path), loaded.LastSaveError);
        Assert.Equal(unreadable, AppSettings.Load(path).Providers[0].ApiKeyProtected);
        Assert.True(Secret.IsProtected(unreadable));
    }

    private static void Edit(string path, Action<JsonNode> change)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        change(root);
        File.WriteAllText(path, root.ToJsonString());
    }
}
