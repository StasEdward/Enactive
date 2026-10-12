namespace Enactive.Engine.Tests;

using Enactive.Core.Providers;
using Enactive.Settings;
using Enactive.Tools.Mcp;

/// <summary>
/// The settings a page edits hold no secret (AppSettings.WithoutSecrets), and a save of what the page sent puts back every
/// secret it left alone (RestoreSecrets).
///
/// <para><b>What went wrong without it.</b> The service's settings page was handed the settings as loaded - every API
/// key, the mail password and each MCP server's tokens decrypted - and kept them in the page's state for as long as the
/// page was open. Nothing drew them, but nothing stopped a box bound to the wrong field from drawing one either.</para>
/// </summary>
public sealed class SettingsWithoutSecretsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "enactive-without-secrets", Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>Settings on disk with one of each secret a page edits, read back as a host reads them.</summary>
    private AppSettings Stored()
    {
        var settings = new AppSettings();
        settings.Providers.Add(new ProviderConfig { Id = "cloud", Kind = ProviderKind.Anthropic, BaseUrl = "https://api.example.com", ApiKey = "sk-very-secret", Models = ["big"] });
        settings.Smtp.Host = "mail.example.com";
        settings.Smtp.Password = "mail-very-secret";
        settings.McpServers.Add(new McpServerConfig
        {
            Id = "tracker", Transport = McpTransportKind.Http, Url = "https://mcp.example.com/",
            Environment = new() { ["TOKEN"] = "env-very-secret" },
            Headers = new() { ["Authorization"] = "Bearer header-very-secret" }
        });
        Assert.True(settings.Save(File, replaceUnreadable: true), settings.LastSaveError);
        return AppSettings.Load(File);
    }

    private static readonly string[] Secrets = ["sk-very-secret", "mail-very-secret", "env-very-secret", "header-very-secret"];

    [WindowsFact]
    public void A_copy_for_a_page_holds_no_secret_and_not_their_stored_forms_either()
    {
        var loaded = Stored();
        var stored = new[] { loaded.Providers[0].ApiKeyProtected, loaded.Smtp.PasswordProtected, loaded.McpServers[0].SecretsProtected };

        var copy = loaded.WithoutSecrets();

        var everything = System.Text.Json.JsonSerializer.Serialize(copy) + string.Join("|",
            copy.Providers[0].ApiKey, copy.Smtp.Password, string.Join(",", copy.McpServers[0].Environment.Values),
            string.Join(",", copy.McpServers[0].Headers.Values));
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, everything));
        Assert.All(stored, ciphertext => Assert.DoesNotContain(ciphertext, everything));
        // What a page may say of them: that each is kept, and the names of a server's credentials.
        Assert.True(AppSettings.IsKept(copy.Providers[0].ApiKeyProtected));
        Assert.True(AppSettings.IsKept(copy.Smtp.PasswordProtected));
        Assert.Equal(["TOKEN"], copy.McpServers[0].Environment.Keys);
        Assert.Equal(["Authorization"], copy.McpServers[0].Headers.Keys);
    }

    /// <summary>Opened, something else changed, saved: a key nobody could see to type back is still there.</summary>
    [WindowsFact]
    public void Every_secret_left_alone_is_saved_as_it_was()
    {
        var copy = Stored().WithoutSecrets();
        copy.KeepRuns = 7;

        Assert.Empty(copy.RestoreSecrets(AppSettings.Load(File)));
        Assert.True(copy.Save(File, replaceUnreadable: true), copy.LastSaveError);

        var saved = AppSettings.Load(File);
        Assert.Equal(7, saved.KeepRuns);
        Assert.Equal("sk-very-secret", saved.Providers[0].ApiKey);
        Assert.Equal("mail-very-secret", saved.Smtp.Password);
        Assert.Equal("env-very-secret", saved.McpServers[0].Environment["TOKEN"]);
        Assert.Equal("Bearer header-very-secret", saved.McpServers[0].Headers["Authorization"]);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, System.IO.File.ReadAllText(File)));
    }

    /// <summary>A secret typed over the blank is the new one; one whose stored form was emptied is removed.</summary>
    [WindowsFact]
    public void A_typed_secret_replaces_and_an_emptied_one_is_removed()
    {
        var copy = Stored().WithoutSecrets();
        copy.Providers[0].ApiKey = "sk-new";
        copy.Smtp.Password = "";
        copy.Smtp.PasswordProtected = "";

        copy.RestoreSecrets(AppSettings.Load(File));
        Assert.True(copy.Save(File, replaceUnreadable: true), copy.LastSaveError);

        var saved = AppSettings.Load(File);
        Assert.Equal("sk-new", saved.Providers[0].ApiKey);
        Assert.Equal("", saved.Smtp.Password);
        Assert.Equal("", saved.Smtp.PasswordProtected);
    }

    /// <summary>
    /// An MCP server's environment and headers are one secret on disk, and each is put back on its own: new headers
    /// typed over a server took its environment with them when the two were restored, or not, together.
    /// </summary>
    [WindowsFact]
    public void New_headers_keep_the_environment_nobody_touched()
    {
        var copy = Stored().WithoutSecrets();
        copy.McpServers[0].Headers = new() { ["Authorization"] = "Bearer header-new" };

        copy.RestoreSecrets(AppSettings.Load(File));

        Assert.Equal("env-very-secret", copy.McpServers[0].Environment["TOKEN"]);
        Assert.Equal("Bearer header-new", copy.McpServers[0].Headers["Authorization"]);
    }

    /// <summary>
    /// A copy restored from settings that no longer hold its secret does not write the handle as one: the secret is left
    /// removed and named, so the caller can refuse the save.
    /// </summary>
    [WindowsFact]
    public void A_secret_the_settings_no_longer_hold_is_named_and_never_written_as_its_handle()
    {
        var copy = Stored().WithoutSecrets();
        var handle = copy.Smtp.PasswordProtected;

        var missing = copy.RestoreSecrets(new AppSettings());

        Assert.Contains("the SMTP password", missing);
        Assert.Equal("", copy.Smtp.PasswordProtected);
        Assert.DoesNotContain(handle, System.Text.Json.JsonSerializer.Serialize(copy));
    }

    /// <summary>
    /// A password this account cannot decrypt is said as one on the copy too - asked by its stored form, since on the
    /// copy every password kept has an empty plaintext beside it - and saved as it was.
    /// </summary>
    [WindowsFact]
    public void A_secret_that_cannot_be_decrypted_is_said_so_on_the_copy_and_kept_on_save()
    {
        Stored();
        // Ciphertext DPAPI cannot open here: what a file copied from another computer holds.
        var unreadable = "dpapi:" + Convert.ToBase64String(new byte[64]);
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(File))!;
        json["Smtp"]!["PasswordProtected"] = unreadable;
        System.IO.File.WriteAllText(File, json.ToJsonString());

        var loaded = AppSettings.Load(File);
        var copy = loaded.WithoutSecrets();

        Assert.True(loaded.CannotDecrypt(loaded.Smtp.PasswordProtected));
        Assert.True(copy.CannotDecrypt(copy.Smtp.PasswordProtected));
        Assert.False(copy.CannotDecrypt(copy.Providers[0].ApiKeyProtected));

        copy.RestoreSecrets(AppSettings.Load(File));
        Assert.True(copy.Save(File, replaceUnreadable: true), copy.LastSaveError);
        Assert.Equal(unreadable, AppSettings.Load(File).Smtp.PasswordProtected);
    }
}
