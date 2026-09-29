namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Builds;
using Enactive.Core.Templates;

/// <summary>A criterion as the planner wrote it, before the engine has accepted it.</summary>
public sealed record PlannedCriterion(string Kind, string? Path, bool NonEmpty, string? Text, string? Target, string Raw)
{
    // covers_all (Phase 5.1): {"source":{"step":0,"field":"pages"},"results":{"step":2,"field":"notes"},"evidence":"file_read"}
    public int? SourceStep { get; init; }
    public string? SourceField { get; init; }
    public int? ResultsStep { get; init; }
    public string? ResultsField { get; init; }
    public string? Evidence { get; init; }

    /// <summary>The plan position of the step it was written inside, or null when it was stated for the whole plan.</summary>
    public int? Step { get; init; }
}

/// <summary>
/// Acceptance criteria the planner states as TYPES - "this file exists", "it says this", "the tests
/// pass" - rather than as shell commands it has to invent (Phase 3).
///
/// <para><b>Why types.</b> A planner proposing "does report.md exist" had to write it as a command,
/// in some shell, on some platform, and a command it guessed wrong could only come back "not
/// checked". A type the engine understands is checked the same way everywhere, for any kind of task
/// (amendment J): a wiki page, a report, a disk inventory - not only code.</para>
///
/// <para><b>Never a way to break a run (3.2).</b> Each criterion is checked for kind, fields and
/// references before it is accepted. One that fails is dropped, with the reason, and the run goes on
/// under the criteria it already had: the system's own checks, and whatever the person and the
/// template asked for. Nor can a planner criterion take any of those away (3.4): they are only ever
/// added to.</para>
/// </summary>
public static class TypedCriteria
{
    /// <summary>What the planner may write, in its words.</summary>
    internal static readonly string[] Kinds = ["file_exists", "file_contains", "tests_pass", "covers_all"];

    /// <summary>The planner's "criteria" list, read leniently: whatever is there is kept for validation to judge.</summary>
    public static IReadOnlyList<PlannedCriterion> Read(JsonElement root)
    {
        // At the top of the plan, where the prompt asks for them - and inside steps, where a planner
        // also puts them. They were read only at the top, so a plan that put "covers_all" and
        // "file_exists" in its steps lost both without a word (run 80c951, 2026-09-28): not accepted,
        // not dropped, not mentioned. A criterion stated twice is one criterion.
        // The step a criterion was written inside is kept: its file criteria are checked when that step ends.
        // Stated twice - in a step and at the top - it is one criterion, in its first place, and it keeps its step.
        var items = new List<JsonElement>();
        var ownedBy = new Dictionary<string, int>(StringComparer.Ordinal);
        if (root.TryGetProperty("criteria", out var top) && top.ValueKind == JsonValueKind.Array)
            items.AddRange(top.EnumerateArray());
        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var step in steps.EnumerateArray())
            {
                if (step.ValueKind == JsonValueKind.Object && step.TryGetProperty("criteria", out var own) && own.ValueKind == JsonValueKind.Array)
                    foreach (var item in own.EnumerateArray())
                    {
                        items.Add(item);
                        ownedBy.TryAdd(JsonSerializer.Serialize(item), index);
                    }
                index++;
            }
        }
        var read = new List<PlannedCriterion>();
        foreach (var item in items.DistinctBy(i => JsonSerializer.Serialize(i)))
        {
            int? owner = ownedBy.TryGetValue(JsonSerializer.Serialize(item), out var o) ? o : null;
            string? Text(string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            (int? Step, string? Field) Ref(string name)
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var r) || r.ValueKind != JsonValueKind.Object)
                    return (null, null);
                return (r.TryGetProperty("step", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) ? n : null,
                    r.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null);
            }
            var (sourceStep, sourceField) = Ref("source");
            var (resultsStep, resultsField) = Ref("results");
            read.Add(new PlannedCriterion(Text("kind") ?? "", Text("path"),
                !(item.ValueKind == JsonValueKind.Object && item.TryGetProperty("non_empty", out var ne) && ne.ValueKind == JsonValueKind.False),
                Text("text"), Text("target"), item.GetRawText())
            {
                SourceStep = sourceStep, SourceField = sourceField, ResultsStep = resultsStep, ResultsField = resultsField,
                Evidence = Text("evidence"), Step = owner
            });
        }
        return read;
    }

    /// <summary>
    /// The planner's criteria the engine accepts, as criteria it can check - and, for each one it does
    /// not, why. A test criterion becomes the recognising ecosystem's test command for each of its test
    /// targets, so it goes through the command gate and the task's contract like any command.
    /// </summary>
    public static (IReadOnlyList<SuccessCriterionDefinition> Accepted, IReadOnlyList<string> Dropped) Validate(
        IReadOnlyList<PlannedCriterion> planned, string workspaceRoot, IReadOnlyList<IEcosystem> ecosystems,
        Enactive.Core.Tasks.Plan? plan = null)
    {
        var accepted = new List<SuccessCriterionDefinition>();
        var dropped = new List<string>();
        void Drop(PlannedCriterion c, string why) => dropped.Add($"Planner criterion dropped - {why}: {c.Raw}");

        foreach (var c in planned)
        {
            switch (c.Kind.Trim().ToLowerInvariant())
            {
                case "file_exists":
                case "file_contains":
                    var contains = c.Kind.Trim().Equals("file_contains", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(c.Path)) { Drop(c, "it names no path"); break; }
                    if (Inside(workspaceRoot, c.Path) is null) { Drop(c, "its path is not inside the workspace"); break; }
                    if (contains && string.IsNullOrEmpty(c.Text)) { Drop(c, "it names no text to look for"); break; }
                    var typed = new TypedCriterion(contains ? TypedCriterionKind.FileContains : TypedCriterionKind.FileExists,
                        Path: c.Path, NonEmpty: c.NonEmpty, Text: c.Text);
                    accepted.Add(new SuccessCriterionDefinition(Name(typed), Describe(typed), 0, Required: true,
                        Origin: CriterionOrigin.Proposed) { Typed = typed, Step = c.Step });
                    break;

                case "tests_pass":
                    var found = ecosystems.Select(e => (Ecosystem: e, Targets: Detect(e, workspaceRoot)))
                        .Where(x => x.Targets is { Tests.Count: > 0 }).ToArray();
                    if (found.Length == 0) { Drop(c, "no ecosystem recognised here has tests to run"); break; }
                    var targets = found.SelectMany(x => x.Targets!.Tests.Select(t => (x.Ecosystem, Target: t)))
                        .Where(x => c.Target is null || string.Equals(x.Target, c.Target, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (targets.Length == 0) { Drop(c, $"'{c.Target}' is not a test target here"); break; }
                    foreach (var (ecosystem, target) in targets)
                        accepted.Add(new SuccessCriterionDefinition($"Tests pass ({target})", ecosystem.TestCommand(target), 0,
                            Required: true, Origin: CriterionOrigin.Proposed)
                            { Typed = new TypedCriterion(TypedCriterionKind.TestsPass, Target: target) });
                    break;

                case "covers_all":
                    var coverage = new TypedCriterion(TypedCriterionKind.EvidenceCoversAll,
                        SourceStep: c.SourceStep, SourceField: c.SourceField, ResultsStep: c.ResultsStep, ResultsField: c.ResultsField,
                        Evidence: EvidenceCoverage.KindNamed(c.Evidence));
                    if (c.Evidence is { } named && coverage.Evidence is null) { Drop(c, $"'{named}' is not an evidence kind (file_read, command, call)"); break; }
                    if (EvidenceCoverage.Invalid(coverage, plan) is { } why) { Drop(c, why); break; }
                    accepted.Add(new SuccessCriterionDefinition(
                        $"Every {c.SourceField} item is covered", EvidenceCoverage.Describe(coverage), 0, Required: true,
                        Origin: CriterionOrigin.Proposed) { Typed = coverage });
                    break;

                case "semantic":
                    Drop(c, "a semantic criterion is the reviewer's to judge, and is not a type the engine checks");
                    break;

                default:
                    Drop(c, $"'{c.Kind}' is not a kind of criterion the engine knows ({string.Join(", ", Kinds)})");
                    break;
            }
        }
        return (accepted, dropped);
    }

    /// <summary>A file criterion, decided by the engine: on disk, as the run leaves it.</summary>
    public static CriterionResult Evaluate(SuccessCriterionDefinition criterion, string workspaceRoot)
    {
        var typed = criterion.Typed!;
        CriterionResult Result(CriterionOutcome outcome, string? detail)
            => new(criterion.Name, criterion.Command, criterion.Required, outcome, null, detail, criterion.Origin, criterion.AlreadyPassing);

        if (Inside(workspaceRoot, typed.Path ?? "") is not { } full)
            return Result(CriterionOutcome.Unknown, "its path is not inside the workspace");
        if (!File.Exists(full))
            return Result(CriterionOutcome.Failed, $"'{typed.Path}' does not exist.");

        try
        {
            if (typed.Kind == TypedCriterionKind.FileExists)
                return typed.NonEmpty && new FileInfo(full).Length == 0
                    ? Result(CriterionOutcome.Failed, $"'{typed.Path}' exists but is empty.")
                    : Result(CriterionOutcome.Passed, null);

            return File.ReadAllText(full).Contains(typed.Text!, StringComparison.Ordinal)
                ? Result(CriterionOutcome.Passed, null)
                : Result(CriterionOutcome.Failed, $"'{typed.Path}' does not contain \"{typed.Text}\".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result(CriterionOutcome.Unknown, $"'{typed.Path}' could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// The file criteria of one step, as they stand now - the ones the engine decides by itself, cheaply, from the
    /// workspace. What a step is checked on when it ends (run 68f92f: a report file the plan named on the last step
    /// was checked only after the run, and the run failed on it).
    /// </summary>
    internal static IReadOnlyList<CriterionResult> OfStep(IEnumerable<SuccessCriterionDefinition> criteria, int planIndex, string workspaceRoot)
        => criteria.Where(c => c.Step == planIndex && c.Typed?.Kind is TypedCriterionKind.FileExists or TypedCriterionKind.FileContains)
            .Select(c => Evaluate(c, workspaceRoot)).ToArray();

    private static string Name(TypedCriterion typed) => typed.Kind == TypedCriterionKind.FileContains
        ? $"{typed.Path} says what it should" : $"{typed.Path} exists";

    private static string Describe(TypedCriterion typed) => typed.Kind == TypedCriterionKind.FileContains
        ? $"file_contains {typed.Path} \"{typed.Text}\""
        : $"file_exists {typed.Path}" + (typed.NonEmpty ? " (not empty)" : "");

    private static EcosystemTargets? Detect(IEcosystem ecosystem, string root)
    {
        try { return ecosystem.Detect(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The full path, when the relative one stays inside the workspace; null otherwise.</summary>
    private static string? Inside(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || System.IO.Path.IsPathRooted(path)) return null;
        try
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
            var within = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            return full.StartsWith(within, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
