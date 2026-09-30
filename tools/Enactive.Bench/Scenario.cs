namespace Enactive.Bench;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// One benchmark task: what is asked, the project it is asked of (the <c>fixture</c> folder next to scenario.json, copied
/// fresh for every run), and the truth it is judged by. The truth is what the run leaves on disk and what its outcome
/// should be - never which steps pass, since a planner makes a different plan every time (plan amendment A, 0b).
/// </summary>
internal sealed record Scenario(
    string Name,
    string Request,
    string Approve,
    Expectation Expect,
    IReadOnlyList<Check> Checks,
    string? About = null)
{
    /// <summary>The folder scenario.json was read from.</summary>
    [JsonIgnore] public string Folder { get; init; } = "";

    public string FixtureFolder => Path.Combine(Folder, "fixture");

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static Scenario Load(string folder)
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(Path.Combine(folder, "scenario.json")), Json)
            ?? throw new InvalidDataException($"{folder}: scenario.json is empty.");
        if (string.IsNullOrWhiteSpace(scenario.Request)) throw new InvalidDataException($"{folder}: no request.");
        if (scenario.Checks is not { Count: > 0 }) throw new InvalidDataException($"{folder}: no checks - nothing to judge it by.");
        return scenario with { Folder = folder };
    }
}

/// <summary>What the run's outcome should be.</summary>
internal sealed record Expectation(string Outcome);

/// <summary>
/// One fact about the workspace after the run. <c>kind</c> is one of file_exists, file_contains, unchanged, command,
/// coverage; the other fields belong to the kinds that use them.
/// </summary>
internal sealed record Check(
    string Id,
    string Kind,
    string? About = null,
    string? Glob = null,
    IReadOnlyList<string>? Exclude = null,
    IReadOnlyList<string>? Patterns = null,
    PatternSource? PatternsFrom = null,
    string? Path = null,
    string? Command = null,
    int? ExitCode = null,
    IReadOnlyList<string>? Contains = null,
    NumberRule? AtLeast = null,
    [property: JsonPropertyName("equals")] NumberRule? Exactly = null,
    IReadOnlyList<CoverageItem>? Items = null,
    int? Min = null,
    bool Required = true);

/// <summary>Patterns computed on this machine at check time - the drives it has, say.</summary>
internal sealed record PatternSource(string Command, string Shell = "cmd");

/// <summary>A number read from output by the first group of a regex, compared with a value.</summary>
internal sealed record NumberRule(string Regex, int Value);

/// <summary>One thing a report should mention, by a regex over it.</summary>
internal sealed record CoverageItem(string Id, string Pattern);
