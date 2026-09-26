namespace Enactive.App.Ui;

using Microsoft.Win32;

/// <summary>HKCU is the source of truth. Call on a worker thread; no shell process is needed.</summary>
internal static class StartupEntry
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Enactive";
    public static bool Supported => OperatingSystem.IsWindows();

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(ValueName) is not null;
        }
        catch { return false; }
    }

    public static bool Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (!enabled) key.DeleteValue(ValueName, throwOnMissingValue: false);
            else
            {
                if (string.IsNullOrEmpty(Environment.ProcessPath)) return false;
                key.SetValue(ValueName, "\"" + Environment.ProcessPath + "\"", RegistryValueKind.String);
            }
            return IsEnabled() == enabled;
        }
        catch { return false; }
    }
}
