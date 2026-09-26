namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Settings;
using Xunit;

public sealed class SettingsRecoveryTests
{
    [Theory]
    [InlineData("{ broken JSON")]
    [InlineData("null")]
    [InlineData("{\"Providers\":null}")]
    public void Automatic_save_and_editor_clone_cannot_replace_unreadable_settings(string original)
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        File.WriteAllText(path, original);
        var settings = AppSettings.Load(path);
        Assert.True(settings.SaveBlocked);
        Assert.NotEmpty(settings.LoadProblems);
        settings.WindowWidth = 1234;
        Assert.False(settings.Save(path));
        Assert.NotNull(settings.LastSaveError);
        Assert.False(settings.Clone().Save(path));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Explicit_recovery_keeps_original_bytes_before_replacing_the_file()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        byte[] original = [0xff, 0x00, 0x7b];
        File.WriteAllBytes(path, original);
        var settings = AppSettings.Load(path).Clone();
        settings.WindowWidth = 1234;
        Assert.True(settings.Save(path, replaceUnreadable: true), settings.LastSaveError);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(fx.Root, "*.bak"))));
        Assert.False(settings.SaveBlocked);
        Assert.Equal(1234, AppSettings.Load(path).WindowWidth);
        Assert.True(settings.Save(path));
        Assert.Single(Directory.GetFiles(fx.Root, "*.bak"));
    }

    [Fact]
    public void Failed_backup_blocks_recovery_and_keeps_the_save_guard()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        File.WriteAllText(path, "{broken");
        var settings = AppSettings.Load(path);
        // Simulate losing access to the original after loading it.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(settings.Save(path, replaceUnreadable: true));
        Assert.True(settings.SaveBlocked);
        Assert.NotNull(settings.LastSaveError);
        using var reader = new StreamReader(exclusive, leaveOpen: true);
        Assert.Equal("{broken", reader.ReadToEnd());
    }

    [Fact]
    public void First_launch_can_save_normally()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("new/settings.json");
        var settings = AppSettings.Load(path);
        Assert.False(settings.SaveBlocked);
        Assert.True(settings.Save(path), settings.LastSaveError);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Unreadable_secrets_survive_load_clone_and_repeated_saves()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        const string cipher = "dpapi:not-valid-base64";
        var original = new AppSettings
        {
            Providers = [new() { Id = "p", ApiKeyProtected = cipher }],
            AnthropicApiKeyProtected = cipher,
            RemoteAccess = new() { TokenProtected = cipher },
            Smtp = new() { PasswordProtected = cipher }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(original));
        var loaded = AppSettings.Load(path).Clone();
        Assert.Empty(loaded.Providers[0].ApiKey);
        Assert.Contains(loaded.LoadProblems, p => p.Contains("provider 'p'"));
        Assert.True(loaded.Save(path), loaded.LastSaveError);
        Assert.True(loaded.Save(path), loaded.LastSaveError);
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        var root = saved.RootElement;
        Assert.Equal(cipher, root.GetProperty("Providers")[0].GetProperty("ApiKeyProtected").GetString());
        Assert.Equal(cipher, root.GetProperty("AnthropicApiKeyProtected").GetString());
        Assert.Equal(cipher, root.GetProperty("RemoteAccess").GetProperty("TokenProtected").GetString());
        Assert.Equal(cipher, root.GetProperty("Smtp").GetProperty("PasswordProtected").GetString());
    }

    [WindowsFact]
    public void Replacing_an_unreadable_key_and_clearing_a_readable_key_still_work()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        var settings = new AppSettings
        {
            Providers = [new() { Id = "p", ApiKeyProtected = "dpapi:invalid", ApiKey = "replacement-test-key" }]
        };
        Assert.True(settings.Save(path), settings.LastSaveError);
        var loaded = AppSettings.Load(path);
        Assert.Equal("replacement-test-key", loaded.Providers[0].ApiKey);
        loaded.Providers[0].ApiKey = "";
        Assert.True(loaded.Save(path), loaded.LastSaveError);
        Assert.Empty(AppSettings.Load(path).Providers[0].ApiKeyProtected);
    }

    [Fact]
    public void Locked_settings_are_not_treated_as_a_first_launch()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        File.WriteAllText(path, "{}");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var settings = AppSettings.Load(path);
        Assert.True(settings.SaveBlocked);
        Assert.False(settings.Save(path));
    }

    [Fact]
    public void Explicit_recovery_can_create_a_missing_settings_file()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("new/settings.json");
        var fallback = new AppSettings();
        fallback.BlockAutomaticSave();
        Assert.False(fallback.Save(path));
        Assert.True(fallback.Save(path, replaceUnreadable: true), fallback.LastSaveError);
        Assert.False(fallback.SaveBlocked);
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.bak"));
    }

    [Fact]
    public async Task Mcp_decryption_failure_disables_runtime_access_without_changing_enabled_preference()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        const string cipher = "dpapi:not-valid-base64";
        File.WriteAllText(path, JsonSerializer.Serialize(new AppSettings
        { McpServers = [new() { Id = "server", Enabled = true, SecretsProtected = cipher }] }));
        var settings = AppSettings.Load(path).Clone();
        var server = Assert.Single(settings.McpServers);
        Assert.True(server.Enabled);
        Assert.True(server.CredentialsUnavailable);
        Assert.NotNull(server.Validate());
        // Invalid credentials are skipped by normal engine assembly rather than failing the entire run.
        await using var tools = await Enactive.Tools.Mcp.McpRunTools.ConnectAsync(
            new Enactive.Tools.ToolRegistry([]), settings.McpServers, fx.Root, default);
        Assert.True(settings.Save(path), settings.LastSaveError);
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        var item = saved.RootElement.GetProperty("McpServers")[0];
        Assert.True(item.GetProperty("Enabled").GetBoolean());
        Assert.Equal(cipher, item.GetProperty("SecretsProtected").GetString());
        Assert.False(item.TryGetProperty("CredentialsUnavailable", out _));
    }

    [Fact]
    public void Runtime_fallback_also_blocks_automatic_save()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("settings.json");
        File.WriteAllText(path, "{\"WindowWidth\":987}");
        var fallback = new AppSettings();
        fallback.BlockAutomaticSave();
        Assert.False(fallback.Clone().Save(path));
        Assert.Contains("987", File.ReadAllText(path));
    }
}
