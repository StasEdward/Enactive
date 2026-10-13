namespace Enactive.Engine.Tests;

using Enactive.Settings;
using Enactive.Core.Storage;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settings_writes_and_versioned_reads_wait_for_another_hosts_lock(bool read)
    {
        var initial = new AppSettings { KeepRuns = 11 };
        Assert.True(initial.Save(Path_), initial.LastSaveError);
        // An independent OS handle stands for a desktop/server in another process, bypassing FileLock's local gate.
        using var otherHost = new FileStream(FileLock.LockPathFor(Path_), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Task.Run(() =>
        {
            started.SetResult();
            if (read)
            {
                var (settings, version) = AppSettings.LoadVersioned(Path_);
                Assert.Equal(11, settings.KeepRuns);
                Assert.NotEqual("none", version);
            }
            else
            {
                var changed = new AppSettings { KeepRuns = 12 };
                Assert.True(changed.Save(Path_), changed.LastSaveError);
            }
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotSame(operation, await Task.WhenAny(operation, Task.Delay(200)));
            Assert.Equal(11, AppSettings.Load(Path_).KeepRuns);
        }
        finally { otherHost.Dispose(); }
        await operation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(read ? 11 : 12, AppSettings.Load(Path_).KeepRuns);
    }

    [Fact]
    public void A_versioned_save_refuses_a_change_written_by_an_unversioned_host()
    {
        var opened = AppSettings.LoadVersioned(Path_);
        Assert.Equal("none", opened.Version);
        var desktop = new AppSettings { KeepRuns = 21 };
        Assert.True(desktop.Save(Path_), desktop.LastSaveError);

        opened.Settings.KeepRuns = 22;
        Assert.False(opened.Settings.Save(Path_, expectedVersion: opened.Version));
        Assert.Contains("changed since you opened them", opened.Settings.LastSaveError);
        Assert.Equal(21, AppSettings.Load(Path_).KeepRuns);
    }

    [Fact]
    public void A_save_that_cannot_exclude_other_processes_changes_nothing()
    {
        var settings = new AppSettings { KeepRuns = 31 };
        Assert.True(settings.Save(Path_), settings.LastSaveError);
        var original = File.ReadAllText(Path_);
        Directory.CreateDirectory(FileLock.LockPathFor(Path_));

        settings.KeepRuns = 32;
        Assert.False(settings.Save(Path_));
        Assert.Contains("Could not lock", settings.LastSaveError);
        Assert.Equal(original, File.ReadAllText(Path_));
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

    /// <summary>
    /// What a connection code leaves in the settings: the gateway and the computer's id in the clear -
    /// the id is no secret, and without it the keys in remote.db would not say whose they are - and
    /// the token only encrypted.
    /// </summary>
    [WindowsFact]
    public void A_connected_computer_keeps_its_id_in_the_clear_and_its_token_encrypted()
    {
        var settings = new AppSettings();
        settings.RemoteAccess.GatewayUrl = "https://remote.example.test";
        settings.RemoteAccess.HostId = "0123456789abcdef0123456789abcdef";
        settings.RemoteAccess.Token = "MARKER-REMOTE-TOKEN";

        Assert.True(settings.Save(Path_), settings.LastSaveError);

        var raw = File.ReadAllText(Path_);
        Assert.Contains("0123456789abcdef0123456789abcdef", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("MARKER-REMOTE-TOKEN", raw, StringComparison.Ordinal);

        var loaded = AppSettings.Load(Path_);
        Assert.Equal("0123456789abcdef0123456789abcdef", loaded.RemoteAccess.HostId);
        Assert.Equal("https://remote.example.test", loaded.RemoteAccess.GatewayUrl);
        Assert.Equal("MARKER-REMOTE-TOKEN", loaded.RemoteAccess.Token);
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
