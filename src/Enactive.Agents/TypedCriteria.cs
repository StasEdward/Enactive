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

    /// <summary>Every evidence kind it names, whether written as one ("file_read") or a list (["file_read","command"]).</summary>
    public IReadOnlyList<string> EvidenceList { get; init; } = [];

    /// <summary>The plan position of the step it was written inside, or null when it was stated for the whole plan.</summary>
    public int? Step { get; init; }

    // file_exists / file_contains over the file a step handed on: {"path_from":{"step":3,"field":"report"}}
    public int? PathFromStep { get; init; }
    public string? PathFromField { get; init; }
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
    internal static readonly string[] Kinds = ["file_exists", "file_contains", "tests_pass", "covers_all", "semantic"];

    /// <summary>A semantic criterion shorter than this says nothing a reviewer could hold a result to.</summary>
    internal const int MinSemanticText = 10;
    internal const int MaxSemanticText = 400;

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
            var (pathFromStep, pathFromField) = Ref("path_from");
            read.Add(new PlannedCriterion(Text("kind") ?? "", Text("path"),
                !(item.ValueKind == JsonValueKind.Object && item.TryGetProperty("non_empty", out var ne) && ne.ValueKind == JsonValueKind.False),
                Text("text"), Text("target"), item.GetRawText())
            {
                SourceStep = sourceStep, SourceField = sourceField, ResultsStep = resultsStep, ResultsField = resultsField,
                Evidence = Text("evidence"), Step = owner, PathFromStep = pathFromStep, PathFromField = pathFromField,
                EvidenceList = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("evidence", out var ev)
                    ? ev.ValueKind == JsonValueKind.Array
                        ? ev.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                        : ev.ValueKind == JsonValueKind.String ? [ev.GetString()!] : []
                    : []
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
        Enactive.Core.Tasks.Plan? plan = null,
        // Phase 1.4: semantic criteria are accepted only where the run judges them (SemanticCriteria).
        bool semantic = false)
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
                    // The file a step hands on, rather than a name fixed in the plan.
                    if (c.PathFromStep is not null || c.PathFromField is not null)
                    {
                        if (HandedPathInvalid(c, plan) is { } notHanded) { Drop(c, notHanded); break; }
                        if (contains && string.IsNullOrEmpty(c.Text)) { Drop(c, "it names no text to look for"); break; }
                        var handed = new TypedCriterion(contains ? TypedCriterionKind.FileContains : TypedCriterionKind.FileExists,
                            NonEmpty: c.NonEmpty, Text: c.Text, PathFromStep: c.PathFromStep, PathFromField: c.PathFromField);
                        accepted.Add(new SuccessCriterionDefinition(Name(handed), Describe(handed), 0, Required: true,
                            Origin: CriterionOrigin.Proposed) { Typed = handed, Step = c.Step });
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(c.Path)) { Drop(c, "it names no path"); break; }
                    if (Inside(workspaceRoot, c.Path) is null) { Drop(c, "its path is not inside the workspace"); break; }
                    if (contains && string.IsNullOrEmpty(c.Text)) { Drop(c, "it names no text to look for"); break; }
                    var typed = new TypedCriterion(contains ? TypedCriterionKind.FileContains : TypedCriterionKind.FileExists,
                        Path: c.Path, NonEmpty: c.NonEmpty, Text: c.Text);
                    accepted.Add(new SuccessCriterionDefinition(Name(typed), Describe(typed), 0, Required: true,
                        Origin: CriterionOrigin.Proposed) { Typed = typed, Step = c.Step });
                    break;

                // The test projects are found when the check runs, not now. Run 89aa8d1b, 2026-10-09: "write tests for the
                // project" - the plan's step 2 made the test project, and its "tests pass" was dropped before step 1 began
                // because there were no tests yet; the run was checked only because the contract review added a command of
                // its own. Found at the end, a test project the run adds is run too.
                case "tests_pass":
                    var tests = new TypedCriterion(TypedCriterionKind.TestsPass, Target: string.IsNullOrWhiteSpace(c.Target) ? null : c.Target.Trim());
                    accepted.Add(new SuccessCriterionDefinition(Name(tests), Describe(tests), 0, Required: true, Origin: CriterionOrigin.Proposed)
                        { Typed = tests });
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
                    if (!semantic) { Drop(c, "a semantic criterion is the reviewer's to judge, and is not a type the engine checks"); break; }
                    var said = c.Text?.Trim() ?? "";
                    if (said.Length < MinSemanticText) { Drop(c, "it states nothing a result could be held to"); break; }
                    if (said.Length > MaxSemanticText) { Drop(c, $"it is longer than {MaxSemanticText} characters - one checkable statement, not a specification"); break; }
                    if (c.Step is null) { Drop(c, "a semantic criterion is judged with the step whose work it is about - state it on that step"); break; }
                    var kinds = c.EvidenceList.Select(k => (Named: k, Kind: EvidenceCoverage.KindNamed(k))).ToArray();
                    if (kinds.FirstOrDefault(k => k.Kind is null) is { Named: { } unknownKind })
                    { Drop(c, $"'{unknownKind}' is not an evidence kind (file_read, command, call)"); break; }
                    var judged = new TypedCriterion(TypedCriterionKind.Semantic, Text: said,
                        Kinds: kinds.Select(k => k.Kind!.Value).Distinct().ToArray());
                    accepted.Add(new SuccessCriterionDefinition(Name(judged), Describe(judged), 0, Required: true,
                        Origin: CriterionOrigin.Proposed) { Typed = judged, Step = c.Step });
                    break;

                default:
                    Drop(c, $"'{c.Kind}' is not a kind of criterion the engine knows ({string.Join(", ", Kinds)})");
                    break;
            }
        }
        return (accepted, dropped);
    }

    /// <summary>Why a path_from criterion cannot be checked in this plan, or null when it can.</summary>
    private static string? HandedPathInvalid(PlannedCriterion c, Enactive.Core.Tasks.Plan? plan)
        => HandedPathInvalid(c.PathFromStep, c.PathFromField, plan);

    internal static string? HandedPathInvalid(int? pathFromStep, string? pathFromField, Enactive.Core.Tasks.Plan? plan)
    {
        if (pathFromStep is not { } step || string.IsNullOrWhiteSpace(pathFromField))
            return "its path_from names no step and field";
        if (plan is null || step < 0 || step >= plan.Steps.Count)
            return $"path_from names step {step}, which is not in the plan";
        if (plan.Steps[step].Output?.Fields.FirstOrDefault(f => f.Name == pathFromField) is not { } field)
            return $"step {step} declares no output field '{pathFromField}' to hand the path on in";
        return field.Type == Enactive.Core.Tasks.StepOutputFieldType.Path ? null
            : $"step {step}'s '{pathFromField}' is not a path (it is {Enactive.Core.Tasks.StepOutputSchema.NameOf(field.Type)})";
    }

    /// <summary>The same criterion over another file: named and described anew, everything else as it was.</summary>
    internal static SuccessCriterionDefinition Rebuild(SuccessCriterionDefinition criterion, TypedCriterion typed)
        => criterion with { Name = Name(typed), Command = Describe(typed), Typed = typed };

    /// <summary>
    /// A file criterion over the file a step HANDED ON: the path its accepted output gave in the field, checked as
    /// any file criterion. No output, or no path in it, fails - and says which.
    /// </summary>
    public static CriterionResult EvaluateHanded(SuccessCriterionDefinition criterion, IEnumerable<Enactive.Core.Tasks.StepOutput> handed,
        string workspaceRoot)
    {
        var typed = criterion.Typed!;
        CriterionResult Failed(string detail)
            => new(criterion.Name, criterion.Command, criterion.Required, CriterionOutcome.Failed, null, detail, criterion.Origin, criterion.AlreadyPassing);
        var output = handed.Where(o => o.StepNo == typed.PathFromStep + 1).OrderByDescending(o => o.Revision).FirstOrDefault();
        if (output is null)
            return Failed($"step {typed.PathFromStep + 1} handed on no result, so there is no '{typed.PathFromField}' to check.");
        string? path;
        try
        {
            using var doc = JsonDocument.Parse(output.ValuesJson);
            path = doc.RootElement.TryGetProperty(typed.PathFromField!, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException) { path = null; }
        if (string.IsNullOrWhiteSpace(path))
            return Failed($"step {typed.PathFromStep + 1} handed on no path in '{typed.PathFromField}'.");
        var result = Evaluate(criterion with { Typed = typed with { Path = path } }, workspaceRoot);
        return result with { Detail = $"'{path}', handed on by step {typed.PathFromStep + 1} as '{typed.PathFromField}'"
                                      + (string.IsNullOrWhiteSpace(result.Detail) ? "." : ": " + result.Detail) };
    }

    internal const int MaxHandedChars = 12_000;

    /// <summary>The file a step handed on as a checked result, as the reviewer is shown it: whole, or its head.</summary>
    internal static string ShowHanded(string path, string field, string workspaceRoot)
    {
        var heading = $"HANDED ON by this step as its '{field}' - '{path}', how it is NOW:";
        if (Inside(workspaceRoot, path) is not { } full) return heading + "\n(the path is not inside the workspace)";
        if (!File.Exists(full)) return heading + "\n(no such file)";
        string text;
        try { text = File.ReadAllText(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return heading + $"\n(it could not be read: {ex.Message})"; }
        return text.Length <= MaxHandedChars ? heading + "\n" + text
            : heading + "\n" + text[..MaxHandedChars] + $"\n[... {text.Length - MaxHandedChars} more characters not shown]";
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
        => criteria.Where(c => c.Step == planIndex && c.Typed is { Kind: TypedCriterionKind.FileExists or TypedCriterionKind.FileContains, PathFromStep: null })
            .Select(c => Evaluate(c, workspaceRoot)).ToArray();

    private static string Name(TypedCriterion typed)
    {
        if (typed.Kind == TypedCriterionKind.Semantic)
            return "judged: " + (typed.Text!.Length <= 80 ? typed.Text : typed.Text[..80] + "…");
        if (typed.Kind == TypedCriterionKind.TestsPass)
            return typed.Target is { } target ? $"Tests pass ({target})" : "Tests pass";
        var file = typed.PathFromStep is { } step ? $"the file step {step + 1} hands on as '{typed.PathFromField}'" : typed.Path;
        return typed.Kind == TypedCriterionKind.FileContains ? $"{file} says what it should" : $"{file} exists";
    }

    /// <summary>The evidence kinds a semantic criterion allows, in its own words.</summary>
    internal static string KindsOf(TypedCriterion typed)
        => typed.Kinds is { Count: > 0 } kinds ? string.Join(", ", kinds.Select(EvidenceCoverage.NameOf)) : "file_read, command, call";

    private static string Describe(TypedCriterion typed)
    {
        if (typed.Kind == TypedCriterionKind.Semantic)
            return $"semantic \"{typed.Text}\" (evidence: {KindsOf(typed)})";
        if (typed.Kind == TypedCriterionKind.TestsPass)
            return typed.Target is { } target
                ? $"the tests of {target}, as found when the final checks run"
                : "the workspace's tests - every test project found when the final checks run";
        var file = typed.PathFromStep is { } step ? $"<step {step + 1}'s {typed.PathFromField}>" : typed.Path;
        return typed.Kind == TypedCriterionKind.FileContains
            ? $"file_contains {file} \"{typed.Text}\""
            : $"file_exists {file}" + (typed.NonEmpty ? " (not empty)" : "");
    }

    /// <summary>Every test project an ecosystem finds in the workspace now, with the ecosystem that runs it.</summary>
    internal static IReadOnlyList<(IEcosystem Ecosystem, string Target)> TestTargets(IReadOnlyList<IEcosystem> ecosystems, string root)
        => ecosystems.SelectMany(e => Detect(e, root)?.Tests.Select(t => (e, t)) ?? []).ToArray();

    private static EcosystemTargets? Detect(IEcosystem ecosystem, string root)
    {
        try { return ecosystem.Detect(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The full path, when the relative one stays inside the workspace; null otherwise.</summary>
    internal static string? Inside(string root, string path)
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
