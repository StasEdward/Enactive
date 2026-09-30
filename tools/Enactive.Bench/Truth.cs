namespace Enactive.Bench;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>How one check came out: passed or not, why, and for a coverage check how much of it was found.</summary>
internal sealed record CheckResult(string Id, string Kind, bool Passed, string Detail, bool Required = true,
    int? Found = null, int? OutOf = null);

/// <summary>
/// The scenario's truth, checked against the workspace a run left - by the bench, not by the engine and not by any
/// model. A check that cannot be carried out (a command that will not start, a regex that does not parse) fails and
/// says why: an unchecked truth is not a passed one.
/// </summary>
internal static class Truth
{
    public static async Task<IReadOnlyList<CheckResult>> CheckAsync(Scenario scenario, string workspace, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        foreach (var check in scenario.Checks)
        {
            CheckResult result;
            try { result = await CheckOneAsync(check, scenario.FixtureFolder, workspace, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new(check.Id, check.Kind, false, $"could not be checked: {ex.Message}");
            }
            results.Add(result with { Required = check.Required });
        }
        return results;
    }

    private static async Task<CheckResult> CheckOneAsync(Check check, string fixture, string workspace, CancellationToken ct)
    {
        switch (check.Kind)
        {
            case "file_exists":
            {
                var files = Files(workspace, Need(check.Glob, "glob"), check.Exclude);
                return new(check.Id, check.Kind, files.Count > 0,
                    files.Count > 0 ? $"{files.Count} file(s): {string.Join(", ", files.Take(5))}" : $"no file matches {check.Glob}");
            }
            case "file_contains":
            {
                var files = Files(workspace, Need(check.Glob, "glob"), check.Exclude);
                if (files.Count == 0) return new(check.Id, check.Kind, false, $"no file matches {check.Glob}");
                var patterns = check.PatternsFrom is { } source ? await PatternsFromAsync(source, workspace, ct)
                    : check.Patterns ?? throw new InvalidDataException("file_contains needs patterns or patternsFrom");
                if (patterns.Count == 0) return new(check.Id, check.Kind, false, "no patterns to look for");
                var text = string.Join("\n", files.Select(f => File.ReadAllText(Path.Combine(workspace, f))));
                var missing = patterns.Where(p => !Regex.IsMatch(text, p)).ToArray();
                return new(check.Id, check.Kind, missing.Length == 0,
                    missing.Length == 0 ? $"all {patterns.Count} pattern(s) in {string.Join(", ", files.Take(5))}"
                        : $"not found: {string.Join(", ", missing)}");
            }
            case "unchanged":
            {
                var path = Need(check.Path, "path");
                var before = Path.Combine(fixture, path);
                var after = Path.Combine(workspace, path);
                if (!File.Exists(after)) return new(check.Id, check.Kind, false, $"{path} is gone");
                var same = Hash(before) == Hash(after);
                return new(check.Id, check.Kind, same, same ? $"{path} as it was" : $"{path} was changed");
            }
            case "command":
            {
                var (exit, output) = await RunAsync("cmd", Need(check.Command, "command"), workspace, ct);
                var problems = new List<string>();
                if (check.ExitCode is { } expected && exit != expected) problems.Add($"exit {exit}, expected {expected}");
                foreach (var needed in check.Contains ?? [])
                    if (!output.Contains(needed, StringComparison.Ordinal)) problems.Add($"no '{needed}' in the output");
                if (check.AtLeast is { } least && Number(output, least.Regex) is var got && (got is null || got < least.Value))
                    problems.Add($"{least.Regex} read {got?.ToString() ?? "nothing"}, expected at least {least.Value}");
                if (check.Exactly is { } equal && Number(output, equal.Regex) is var read && read != equal.Value)
                    problems.Add($"{equal.Regex} read {read?.ToString() ?? "nothing"}, expected {equal.Value}");
                return new(check.Id, check.Kind, problems.Count == 0,
                    problems.Count == 0 ? $"exit {exit}, as expected" : string.Join("; ", problems));
            }
            case "coverage":
            {
                var items = check.Items ?? throw new InvalidDataException("coverage needs items");
                var files = Files(workspace, Need(check.Glob, "glob"), check.Exclude);
                var text = string.Join("\n", files.Select(f => File.ReadAllText(Path.Combine(workspace, f))));
                var found = items.Where(i => Regex.IsMatch(text, i.Pattern)).Select(i => i.Id).ToArray();
                var min = check.Min ?? items.Count;
                return new(check.Id, check.Kind, found.Length >= min,
                    $"{found.Length} of {items.Count} found"
                    + (found.Length < items.Count ? $"; missed: {string.Join(", ", items.Select(i => i.Id).Except(found))}" : ""),
                    Found: found.Length, OutOf: items.Count);
            }
            default:
                throw new InvalidDataException($"unknown check kind '{check.Kind}'");
        }
    }

    private static string Need(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"the check needs '{name}'") : value;

    /// <summary>The first group of the LAST match - a test runner's summary comes at the end.</summary>
    internal static int? Number(string output, string regex)
        => Regex.Matches(output, regex) is { Count: > 0 } matches && int.TryParse(matches[^1].Groups[1].Value, out var n) ? n : null;

    private static async Task<IReadOnlyList<string>> PatternsFromAsync(PatternSource source, string workspace, CancellationToken ct)
    {
        var (exit, output) = await RunAsync(source.Shell, source.Command, workspace, ct);
        if (exit != 0) throw new InvalidOperationException($"the pattern command exited {exit}: {output.Trim()}");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static async Task<(int Exit, string Output)> RunAsync(string shell, string command, string workspace, CancellationToken ct)
    {
        var psi = shell.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command } }
            : new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", command } };
        psi.WorkingDirectory = workspace;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.UTF8;
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{shell} did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout + await stderr);
    }

    /// <summary>Workspace-relative files, with '/', that a glob names and no exclusion does; never under .git.</summary>
    internal static IReadOnlyList<string> Files(string root, string glob, IReadOnlyList<string>? exclude)
    {
        var include = GlobRegex(glob);
        var excluded = (exclude ?? []).Select(GlobRegex).ToArray();
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(".git/", StringComparison.Ordinal))
            .Where(f => include.IsMatch(f) && !excluded.Any(x => x.IsMatch(f)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>A glob as a regex: ** any folders, * within one, ? one character, {a,b} either.</summary>
    internal static Regex GlobRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var slash = i + 2 < glob.Length && glob[i + 2] == '/';
                sb.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else if (c == '{')
            {
                var end = glob.IndexOf('}', i);
                if (end < 0) { sb.Append(Regex.Escape("{")); continue; }
                sb.Append("(?:").Append(string.Join("|", glob[(i + 1)..end].Split(',').Select(Regex.Escape))).Append(')');
                i = end;
            }
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
