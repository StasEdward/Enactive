namespace Enactive.Workspace;

using System.Text.RegularExpressions;
using Enactive.Core.Builds;

/// <summary>
/// .NET, as the engine needs to know it: whether a workspace is one, what to build and test, which
/// changed files can change the build, and how to read what <c>dotnet build</c> printed.
///
/// <para>The parsing is fitted to real output captured on this machine on 2026-09-28, not to a
/// description of the format - see <see cref="DiagnosticSet"/> for the two things that capture
/// showed and a guess would have missed.</para>
/// </summary>
public sealed class DotnetEcosystem : IEcosystem
{
    public string Name => "dotnet";

    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    private static readonly HashSet<string> OwnedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".sln", ".slnx",
        ".razor", ".cshtml", ".xaml", ".axaml", ".resx"
    };

    // Named files that change how everything builds, whatever their extension says.
    private static readonly HashSet<string> OwnedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "global.json", "nuget.config"
    };

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "node_modules", ".enactive"
    };

    private const int SearchDepth = 3;

    public EcosystemTargets? Detect(string workspaceRoot)
    {
        if (!Directory.Exists(workspaceRoot)) return null;

        var projects = ProjectFiles(workspaceRoot).ToArray();
        // A solution at the root builds everything under it in one command, and names the set the
        // workspace's author meant; without one, every project found is a target of its own.
        var solutions = Directory.EnumerateFiles(workspaceRoot)
            .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (projects.Length == 0 && solutions.Length == 0) return null;

        var build = (solutions.Length > 0 ? solutions : projects)
            .Select(f => Relative(workspaceRoot, f)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var tests = projects.Where(IsTestProject)
            .Select(f => Relative(workspaceRoot, f)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        return new EcosystemTargets(Name, build, tests);
    }

    public bool Owns(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return OwnedNames.Contains(name) || OwnedExtensions.Contains(Path.GetExtension(relativePath));
    }

    public string BuildCommand(string target) => $"dotnet build \"{target}\" -nologo";

    public string TestCommand(string target) => $"dotnet test \"{target}\" -nologo";

    // `path(line,col): error CS0103: message`, the position optionally a range `(l,c,l2,c2)`.
    private static readonly Regex Positioned = new(
        @"^\s*(?<file>.+?)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\)\s*:\s*(?<sev>\w+)\s+(?<code>[A-Za-z]{1,12}\d+)\s*:\s*(?<msg>.*)$",
        RegexOptions.Compiled);

    // `MSBUILD : error MSB1009: message`, or a project path in place of MSBUILD, with no position.
    private static readonly Regex Unpositioned = new(
        @"^\s*(?<origin>.+?)\s*:\s*(?<sev>\w+)\s+(?<code>[A-Za-z]{1,12}\d+)\s*:\s*(?<msg>.*)$",
        RegexOptions.Compiled);

    // The project MSBuild appends to a compiler diagnostic, `[...\Probe.csproj]`, with the target
    // framework after `::` when there is more than one. It is WHICH BUILD said it, not what was said,
    // and it differs between the copies of one error that a multi-targeted project prints.
    private static readonly Regex ProjectSuffix = new(
        @"\s+\[[^\]]*\.(?:csproj|fsproj|vbproj|proj|sln|slnx)(?:::[^\]]*)?\]\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string output, string workspaceRoot)
    {
        var found = new List<BuildDiagnostic>();
        foreach (var raw in output.Split('\n'))
        {
            var line = ProjectSuffix.Replace(raw.TrimEnd('\r'), "");
            var positioned = Positioned.Match(line);
            if (positioned.Success && Severity(positioned.Groups["sev"].Value) is { } severity)
            {
                found.Add(new BuildDiagnostic(Name, Relative(workspaceRoot, positioned.Groups["file"].Value.Trim()),
                    positioned.Groups["code"].Value, severity, Message(positioned.Groups["msg"].Value),
                    int.Parse(positioned.Groups["line"].Value), int.Parse(positioned.Groups["col"].Value)));
                continue;
            }
            var unpositioned = Unpositioned.Match(line);
            if (unpositioned.Success && Severity(unpositioned.Groups["sev"].Value) is { } other)
            {
                var origin = unpositioned.Groups["origin"].Value.Trim();
                found.Add(new BuildDiagnostic(Name, LooksLikePath(origin) ? Relative(workspaceRoot, origin) : null,
                    unpositioned.Groups["code"].Value, other, Message(unpositioned.Groups["msg"].Value)));
            }
        }
        return found;
    }

    /// <summary>
    /// The severity word, English as this machine prints it and Russian as a Russian-language SDK
    /// does. Anything else is not a diagnostic line - "Build succeeded", a restore message - and is
    /// skipped rather than guessed at.
    /// </summary>
    private static DiagnosticSeverity? Severity(string word) => word.ToLowerInvariant() switch
    {
        "error" or "ошибка" => DiagnosticSeverity.Error,
        "warning" or "предупреждение" => DiagnosticSeverity.Warning,
        _ => null
    };

    private static string Message(string text) => Spaces.Replace(text, " ").Trim();

    private static bool LooksLikePath(string origin)
        => origin.Contains('\\') || origin.Contains('/') || Path.HasExtension(origin);

    private static string Relative(string root, string path)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(root, path);
        var relative = Path.GetRelativePath(root, full);
        // Outside the workspace stays absolute: a relative path climbing out with ".." would read as
        // part of the project, and the same file could then be named two ways.
        return (relative.StartsWith("..", StringComparison.Ordinal) ? full : relative).Replace('\\', '/');
    }

    private static IEnumerable<string> ProjectFiles(string root)
    {
        var pending = new Stack<(string Folder, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (folder, depth) = pending.Pop();
            IEnumerable<string> files, folders;
            try
            {
                files = Directory.EnumerateFiles(folder);
                folders = Directory.EnumerateDirectories(folder);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }

            foreach (var file in files)
                if (ProjectExtensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    yield return file;
            if (depth >= SearchDepth) continue;
            foreach (var sub in folders)
                if (!SkippedFolders.Contains(Path.GetFileName(sub)))
                    pending.Push((sub, depth + 1));
        }
    }

    private static bool IsTestProject(string projectFile)
    {
        string text;
        try { text = File.ReadAllText(projectFile); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
        return Regex.IsMatch(text, @"<IsTestProject>\s*true\s*</IsTestProject>", RegexOptions.IgnoreCase)
               || Regex.IsMatch(text, @"Include\s*=\s*""(Microsoft\.NET\.Test\.Sdk|xunit|xunit\.v3|NUnit|MSTest\.TestFramework|MSTest)""",
                   RegexOptions.IgnoreCase);
    }
}
