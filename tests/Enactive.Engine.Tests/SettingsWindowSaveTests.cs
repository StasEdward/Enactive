namespace Enactive.Engine.Tests;

using Enactive.App.Ui;
using Enactive.Settings;
using Xunit;

/// <summary>
/// Saving the settings window changes Windows start-up last, and only when the settings were saved.
///
/// <para><b>Why.</b> Save used to write the Run key first, then validate, then write the file. A
/// save that was rejected - two providers with one id, a file that could not be written - said
/// "Not saved" over a start-up entry it had already changed, and nothing put it back.</para>
///
/// <para>The start-up entry here is a fake: the real one is the registry of whoever runs the tests.</para>
/// </summary>
public sealed class SettingsWindowSaveTests
{
    [Fact]
    public async Task Settings_that_do_not_validate_leave_startup_alone()
    {
        var startup = new FakeStartup(enabled: false);
        var saves = 0;

        var result = await SettingsSave.RunAsync(Invalid(), _ => saves++, startup, requestedStartup: true);

        Assert.False(result.Saved);
        Assert.StartsWith("Not saved", result.Note);
        Assert.Equal(0, saves);
        Assert.Empty(startup.SetCalls);
    }

    [Fact]
    public async Task A_file_that_could_not_be_written_leaves_startup_alone()
    {
        var startup = new FakeStartup(enabled: false);

        var result = await SettingsSave.RunAsync(
            new AppSettings(), _ => throw new InvalidOperationException("disk full"), startup, requestedStartup: true);

        Assert.False(result.Saved);
        Assert.Equal("Not saved — disk full", result.Note);
        Assert.Empty(startup.SetCalls);
    }

    [Fact]
    public async Task Startup_is_changed_once_as_asked_after_the_settings_are_saved()
    {
        var order = new List<string>();
        var startup = new FakeStartup(enabled: false) { OnSet = () => order.Add("startup") };

        var result = await SettingsSave.RunAsync(
            new AppSettings(), _ => order.Add("saved"), startup, requestedStartup: true);

        Assert.True(result.Saved);
        Assert.False(result.StartupRefused);
        Assert.Equal(string.Empty, result.Note);
        Assert.Equal(new[] { true }, startup.SetCalls);
        Assert.Equal(new[] { "saved", "startup" }, order);
    }

    [Fact]
    public async Task Startup_that_is_already_as_asked_is_not_written_again()
    {
        var startup = new FakeStartup(enabled: true);

        var result = await SettingsSave.RunAsync(new AppSettings(), _ => { }, startup, requestedStartup: true);

        Assert.True(result.Saved);
        Assert.Empty(startup.SetCalls);
    }

    [Fact]
    public async Task A_refused_startup_change_says_so_and_the_settings_are_still_saved()
    {
        var startup = new FakeStartup(enabled: false) { Refuses = true };
        var saves = 0;

        var result = await SettingsSave.RunAsync(new AppSettings(), _ => saves++, startup, requestedStartup: true);

        Assert.True(result.Saved);
        Assert.Equal(1, saves);
        Assert.True(result.StartupRefused);
        // The real state, not the one asked for: the box goes back to what the system has.
        Assert.False(result.StartupEnabled);
        Assert.Equal(new[] { true }, startup.SetCalls);
        Assert.Contains("saved", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Start-up is left as it was", result.Note);
    }

    [Fact]
    public async Task A_desktop_without_startup_is_never_asked()
    {
        var startup = new FakeStartup(enabled: false) { Supported = false };

        var result = await SettingsSave.RunAsync(new AppSettings(), _ => { }, startup, requestedStartup: true);

        Assert.True(result.Saved);
        Assert.False(result.StartupRefused);
        Assert.Empty(startup.SetCalls);
    }

    /// <summary>Two providers with one id: the configuration Validate was written to stop.</summary>
    private static AppSettings Invalid()
    {
        var settings = new AppSettings();
        settings.Providers.Add(new ProviderConfig { Id = "same", BaseUrl = "http://localhost:1/v1" });
        settings.Providers.Add(new ProviderConfig { Id = "same", BaseUrl = "http://localhost:2/v1" });
        Assert.NotEmpty(settings.Validate());
        return settings;
    }

    private sealed class FakeStartup(bool enabled) : IStartupEntry
    {
        private bool _enabled = enabled;

        public bool Supported { get; init; } = true;
        public bool Refuses { get; init; }
        public Action? OnSet { get; init; }
        public List<bool> SetCalls { get; } = new();

        public bool IsEnabled() => _enabled;

        public bool Set(bool enabled)
        {
            SetCalls.Add(enabled);
            OnSet?.Invoke();
            if (Refuses) return false;
            _enabled = enabled;
            return true;
        }
    }
}
