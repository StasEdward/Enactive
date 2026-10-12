namespace Enactive.Server.Tests;

/// <summary>
/// The folder of the repository, for a test that reads a source file as text. Found by walking up from where the tests
/// run to the folder that holds Enactive.sln, rather than counting "..": the depth under bin/ differs by configuration
/// and by how the tests are started, and a wrong guess reads as a missing file, not as a wrong path.
/// </summary>
internal static class RepoRoot
{
    private static readonly Lazy<string> Found = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Enactive.sln")))
                return dir.FullName;
        throw new DirectoryNotFoundException($"No Enactive.sln above {AppContext.BaseDirectory}.");
    });

    public static string Path => Found.Value;

    /// <summary>A file under the repository, by the segments below its root.</summary>
    public static string Source(params string[] segments) => System.IO.Path.Combine([Path, .. segments]);
}
