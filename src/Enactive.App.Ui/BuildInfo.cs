namespace Enactive.App.Ui;

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

/// <summary>One line of the About panel: what it is, and which version of it.</summary>
internal sealed record BuildComponent(string Name, string Version, string Detail);

/// <summary>
/// What this build actually is. Everything here is read from the files on disk rather than from a
/// number typed into the app: a version the app states about itself is a claim, and a version read
/// off the assembly beside it is a fact.
///
/// <para>The components all ship from one repo and one build, so they carry the same number. That
/// sameness is the information: two different versions in that list mean a stale DLL is sitting in
/// the output folder, which is the failure this panel exists to catch.</para>
/// </summary>
internal static class BuildInfo
{
    /// <summary>Third-party assemblies worth naming - the ones whose version changes what the app
    /// can do, and which are updated independently of it.</summary>
    private static readonly string[] Frameworks =
    {
        "Avalonia.Base", "Avalonia.Controls", "Avalonia.Desktop", "Avalonia.Themes.Fluent",
        "Microsoft.Data.Sqlite", "MySqlConnector"
    };

    /// <summary>"0.1.0+9c6cd2b" when the build could see git, "0.1.0" when it could not.</summary>
    public static string AppVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly();
            var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(informational)
                ? assembly?.GetName().Version?.ToString() ?? "unknown"
                : informational;
        }
    }

    /// <summary>When the running binary was written. The build date without anyone having to
    /// remember to stamp one.</summary>
    public static string BuiltAt
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
                return string.IsNullOrEmpty(path)
                    ? "unknown"
                    : File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return "unknown";
            }
        }
    }

    public static string Runtime =>
        $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}";

    /// <summary>
    /// The app's own assemblies and the frameworks it ships with, read from the folder the app is
    /// running out of - so a DLL that was never loaded this session is still listed, which is
    /// exactly the one worth spotting.
    /// </summary>
    public static List<BuildComponent> Components()
    {
        var rows = new List<BuildComponent>();

        var directory = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return rows;

        foreach (var file in Directory.EnumerateFiles(directory, "Enactive.*.dll").OrderBy(f => f))
            Add(rows, file);

        foreach (var name in Frameworks)
        {
            var file = Path.Combine(directory, name + ".dll");
            if (File.Exists(file))
                Add(rows, file);
        }

        return rows;
    }

    private static void Add(List<BuildComponent> rows, string file)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            var version = string.IsNullOrWhiteSpace(info.ProductVersion)
                ? AssemblyName.GetAssemblyName(file).Version?.ToString() ?? "?"
                : info.ProductVersion;

            rows.Add(new BuildComponent(
                Path.GetFileNameWithoutExtension(file),
                version,
                File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm")));
        }
        catch
        {
            // A file that cannot be read is still worth listing - unreadable is a state too.
            rows.Add(new BuildComponent(Path.GetFileNameWithoutExtension(file), "unreadable", string.Empty));
        }
    }
}
