namespace Enactive.Engine.Tests;

using Enactive.Settings;
using Enactive.Tools.Mcp;
using Xunit;

/// <summary>
/// <see cref="AppSettings.Save"/>, run for real against a temp file - the whole path, not each piece
/// of it separately.
///
/// <para><b>Why this did not exist.</b> <c>Load</c> had a <c>path</c> parameter so a test could point
/// it at a fixture; <c>Save</c> did not, so exercising it meant either writing to the developer's
/// real %APPDATA% or not testing it at all. The unit tests for encryption, JSON attributes and
/// loading each check one piece; none of them runs the SEQUENCE <c>Save</c> actually is - pack MCP
/// credentials, encrypt every family of secret, blank the legacy plaintext for serialization, write
/// a temp file, replace the real one, restore the plaintext in memory - so a mistake in how those
/// steps fit together would not have
/// been caught by any of them.</para>
///
/// <para>Real secrets are never asserted here - only artificial markers that would be conspicuous if
/// they leaked, and only in a throwaway temp directory.</para>
/// </summary>
public sealed class SettingsSaveTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "enactive-settings-save", Guid.NewGuid().ToString("N"));

    private string Path_ => System.IO.Path.Combine(_dir, "settings.json");

    public SettingsSaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>THE MAIN CASE: every family of secret round-trips through Save then Load, and none
    /// of the plaintext markers ever reaches the file on disk.</summary>
    [WindowsFact]
    public void Every_family_of_secret_round_trips_and_none_of_it_is_written_in_the_clear()
    {

        var settings = new AppSettings
        {
            Providers = new List<ProviderConfig> { new() { Id = "p1", ApiKey = "MARKER-PROVIDER-KEY" } },
            AnthropicApiKey = "MARKER-LEGACY-KEY",
        };
        settings.RemoteAccess.Token = "MARKER-REMOTE-TOKEN";
        settings.Smtp.Password = "MARKER-SMTP-PASSWORD";

        Assert.True(settings.Save(Path_), settings.LastSaveError);

        var raw = File.ReadAllText(Path_);
        Assert.DoesNotContain("MARKER-PROVIDER-KEY", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("MARKER-LEGACY-KEY", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("MARKER-REMOTE-TOKEN", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("MARKER-SMTP-PASSWORD", raw, StringComparison.Ordinal);

        var loaded = AppSettings.Load(Path_);
        Assert.Equal("MARKER-PROVIDER-KEY", Assert.Single(loaded.Providers).ApiKey);
        Assert.Equal("MARKER-LEGACY-KEY", loaded.AnthropicApiKey);
        Assert.Equal("MARKER-REMOTE-TOKEN", loaded.RemoteAccess.Token);
        Assert.Equal("MARKER-SMTP-PASSWORD", loaded.Smtp.Password);
    }

    /// <summary>The plaintext field is restored in memory after Save, not left blank - it is only
    /// SERIALIZATION that must never see it.</summary>
    [WindowsFact]
    public void The_legacy_plaintext_is_still_there_in_memory_after_saving()
    {

        var settings = new AppSettings { AnthropicApiKey = "MARKER-LEGACY-KEY" };

        settings.Save(Path_);

        Assert.Equal("MARKER-LEGACY-KEY", settings.AnthropicApiKey);
    }

    /// <summary>An MCP server the app cannot decrypt keeps its ORIGINAL ciphertext exactly - Save
    /// must not re-encrypt an empty Environment/Headers over it and lose what a person cannot
    /// currently read back.</summary>
    [WindowsFact]
    public void An_mcp_servers_unreadable_ciphertext_survives_a_save_untouched()
    {

        const string originalCiphertext = "dpapi:this-is-not-decryptable-by-this-test";
        var settings = new AppSettings
        {
            McpServers = new List<McpServerConfig>
            {
                new() { Id = "srv", SecretsProtected = originalCiphertext, CredentialsUnavailable = true }
            }
        };

        Assert.True(settings.Save(Path_), settings.LastSaveError);

        Assert.Equal(originalCiphertext, settings.McpServers[0].SecretsProtected);
        Assert.Contains(originalCiphertext, File.ReadAllText(Path_), StringComparison.Ordinal);
    }

    /// <summary>An MCP server WITH readable credentials is re-encrypted normally and round-trips.</summary>
    [WindowsFact]
    public void An_mcp_servers_readable_credentials_round_trip()
    {

        var settings = new AppSettings
        {
            McpServers = new List<McpServerConfig>
            {
                new()
                {
                    Id = "srv",
                    Environment = new Dictionary<string, string> { ["KEY"] = "MARKER-ENV-VALUE" },
                    Headers = new Dictionary<string, string> { ["X-Token"] = "MARKER-HEADER-VALUE" }
                }
            }
        };

        Assert.True(settings.Save(Path_), settings.LastSaveError);
        Assert.DoesNotContain("MARKER-ENV-VALUE", File.ReadAllText(Path_), StringComparison.Ordinal);

        var loaded = AppSettings.Load(Path_);
        var server = Assert.Single(loaded.McpServers);
        Assert.False(server.CredentialsUnavailable);
        Assert.Equal("MARKER-ENV-VALUE", server.Environment["KEY"]);
        Assert.Equal("MARKER-HEADER-VALUE", server.Headers["X-Token"]);
    }

    /// <summary>
    /// A save that fails partway - forced here by making the TEMP file's own path an existing
    /// directory, so the write throws before anything at the real path is touched, with no need to
    /// provoke DPAPI itself - reports it and leaves the previous file exactly as it was.
    /// </summary>
    [Fact]
    public void A_failed_save_reports_it_and_leaves_the_previous_file_untouched()
    {
        var first = new AppSettings { AnthropicWorkspaceId = "first-save" };
        Assert.True(first.Save(Path_), first.LastSaveError);
        var savedBefore = File.ReadAllText(Path_);

        // The write goes to `path + ".tmp"` then moves it into place. Making that name an existing
        // DIRECTORY makes File.WriteAllText throw before the real file is ever touched.
        Directory.CreateDirectory(Path_ + ".tmp");

        var second = new AppSettings { AnthropicWorkspaceId = "second-save-must-not-land" };
        var ok = second.Save(Path_);

        Assert.False(ok);
        Assert.NotNull(second.LastSaveError);
        Assert.Equal(savedBefore, File.ReadAllText(Path_));
    }
}
