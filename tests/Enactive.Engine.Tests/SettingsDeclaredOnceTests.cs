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

    /// <summary>
    /// Every switch the engine has is read from the setting of the same name - set each to something other than
    /// its default, and each must arrive. A switch read from the wrong setting, or left at its default, fails here.
    /// </summary>
    [Fact]
    public void Every_engine_switch_is_read_from_its_own_setting()
    {
        var settings = new AppSettings();
        var expected = new Dictionary<string, object?>();
        var n = 0;
        foreach (var option in typeof(EngineOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != nameof(EngineOptions.FanOut)))
        {
            var setting = typeof(AppSettings).GetProperty(option.Name)
                          ?? throw new Xunit.Sdk.XunitException($"No setting is named {option.Name}.");
            var value = Different(option.PropertyType, option.GetValue(EngineOptions.Default), ++n);
            setting.SetValue(settings, value);
            expected[option.Name] = value;
        }
        settings.MaxStepsPerExpansion = 91;
        settings.MaxTotalSteps = 92;
        settings.MaxFanOutDepth = 93;

        var options = EngineComposition.Options(settings);

        foreach (var (name, value) in expected)
            Assert.Equal(value, typeof(EngineOptions).GetProperty(name)!.GetValue(options));
        Assert.Equal(new FanOutLimits(91, 92, 93), options.FanOut);
    }

    /// <summary>The product's defaults are the settings' defaults: one set, not two.</summary>
    [Fact]
    public void A_new_installation_runs_on_the_engine_s_own_defaults()
        => Assert.Equal(EngineOptions.Default, EngineComposition.Options(new AppSettings()));

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
