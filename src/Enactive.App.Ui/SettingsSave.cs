namespace Enactive.App.Ui;

using Enactive.Settings;

/// <summary>
/// What a Save came to. <paramref name="Saved"/> is about the settings file; start-up is a separate
/// answer, because one can succeed while the other is refused.
/// </summary>
/// <param name="Note">What the person has to be told, or empty when everything asked for happened.</param>
/// <param name="StartupRefused">The settings were saved and start-up could not be changed.</param>
/// <param name="StartupEnabled">Start-up as the system has it now. Read only when it was refused.</param>
internal sealed record SettingsSaveResult(bool Saved, string Note, bool StartupRefused, bool StartupEnabled);

/// <summary>
/// The steps of saving the settings window, in the order they have to happen. Apart from the view
/// model so the order can be tested without a window.
/// </summary>
internal static class SettingsSave
{
    public static async Task<SettingsSaveResult> RunAsync(
        AppSettings working, Action<AppSettings> onSaved, IStartupEntry startup, bool requestedStartup)
    {
        // Validate BEFORE handing this over to be written. A configuration that cannot be built —
        // two providers with the same id, say — used to be saved anyway, and then took the app down
        // on every launch afterwards, because startup reads the same file and fails the same way.
        var problems = working.Validate();
        if (problems.Count > 0)
            return new(false, "Not saved — " + string.Join(" ", problems), false, false);

        try { onSaved(working); }
        catch (Exception ex) { return new(false, "Not saved — " + ex.Message, false, false); }

        // Start-up LAST, once the settings are written. It used to be changed first, so a save that
        // was then rejected said "Not saved" over a Run key it had already rewritten, and nothing
        // put the key back. In this order a rejected save changes nothing outside the window.
        //
        // Off the calling thread: the real entry is the registry, and the caller is the UI.
        var startupRefused = await Task.Run(() => startup.Supported
            && requestedStartup != startup.IsEnabled()
            && !startup.Set(requestedStartup));

        if (!startupRefused)
            return new(true, string.Empty, false, false);

        // Says both halves, because they now differ: the file was written and start-up was not. And
        // reads the entry back, so the box shows what happened rather than a tick that means nothing.
        return new(true,
            "Settings are saved. Windows would not let start-up be changed. Start-up is left as it was.",
            true, await Task.Run(startup.IsEnabled));
    }
}
