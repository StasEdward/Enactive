namespace Enactive.App.Ui;

using System.Diagnostics;

/// <summary>
/// Whether Windows starts Enactive at login, as an entry under HKCU's Run key.
///
/// <para>The registry IS the state - nothing about it is mirrored into settings.json. A duplicate
/// would drift the moment the user removed the entry with Task Manager or msconfig, and the
/// checkbox would then be confidently wrong about something they can see for themselves.</para>
///
/// <para>Driven through reg.exe rather than a registry API so the app needs no extra package for
/// one key, and so the operation is exactly the one a person would type. HKCU, never HKLM: this is
/// the user's own login, and it needs no elevation.</para>
/// </summary>
internal static class StartupEntry
{
    private const string Key = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Enactive";

    /// <summary>Windows only. Elsewhere the setting is shown but disabled rather than hidden -
    /// "this desktop does not do that" is more use than a row that quietly is not there.</summary>
    public static bool Supported => OperatingSystem.IsWindows();

    public static bool IsEnabled()
    {
        if (!Supported)
            return false;

        // Exit code 0 means the value is there; 1 means it is not. Neither is an error.
        return Run($"query \"{Key}\" /v {ValueName}") == 0;
    }

    /// <summary>Adds or removes the entry. Returns false if the registry refused, so the caller can
    /// say so instead of leaving a checkbox claiming something that did not happen.</summary>
    public static bool Set(bool enabled)
    {
        if (!Supported)
            return false;

        if (!enabled)
        {
            // Deleting a value that is not there exits 1. Already absent is the wanted state.
            Run($"delete \"{Key}\" /v {ValueName} /f");
            return !IsEnabled();
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;

        // The path is quoted inside the value, or a Program Files path starts the wrong program.
        // \" escapes a quote for reg.exe's own parser; /f overwrites an entry pointing at an older
        // install rather than failing on it.
        return Run($"add \"{Key}\" /v {ValueName} /t REG_SZ /d \"\\\"{exe}\\\"\" /f") == 0;
    }

    private static int Run(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("reg.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
                return -1;

            // Read both pipes before waiting: reg.exe says little, but a full pipe deadlocks
            // whatever is on the other end of it.
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            // No reg.exe, or policy said no. The caller reports it; the app carries on.
            return -1;
        }
    }
}
