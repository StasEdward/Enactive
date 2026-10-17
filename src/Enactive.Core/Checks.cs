namespace Enactive.Core.Checks;

using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// When a check runs (Plan Phase 3). Stored by NAME, as every enum the engine persists is: a checkpoint or a run record
/// written today must still mean the same thing after a member is added.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckFire>))]
public enum CheckFire
{
    /// <summary>After a successful write that changed a path its <see cref="CheckDefinition.Matches"/> cover - always advisory.</summary>
    Write,

    /// <summary>After a step that changed a path its <see cref="CheckDefinition.Matches"/> cover.</summary>
    Step,

    /// <summary>With the run's final checks. May cover no paths: it runs once, whatever changed.</summary>
    Run
}

/// <summary>What a failed check does to the work it judged. By name, for the same reason as <see cref="CheckFire"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckSeverity>))]
public enum CheckSeverity
{
    /// <summary>Evidence for the reviewer; it decides nothing on its own. The default: blocking is opted into per check.</summary>
    Advisory,

    /// <summary>A failure fails the step (or the run), without asking the reviewer.</summary>
    Blocking
}

/// <summary>Where a resolved check came from, narrowest last - like a template's origin.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckOrigin>))]
public enum CheckOrigin
{
    /// <summary>The engine's own build or test of a workspace an ecosystem recognises (<c>build</c>, <c>test</c>).</summary>
    Implicit,

    /// <summary>%APPDATA%/Enactive/checks - every workspace on this machine.</summary>
    Global,

    /// <summary>&lt;workspace&gt;/.enactive/checks - lives with the project.</summary>
    Workspace
}

/// <summary>A place check manifests are written to. An implicit check is in neither: it is the engine's.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckScope>))]
public enum CheckScope
{
    Global,
    Workspace
}

/// <summary>
/// The boundary a check is owed at (Plan Phase 3.2/3.3): <c>write|step|wave|apply|run</c>. By name, for the same reason
/// as <see cref="CheckFire"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckBoundary>))]
public enum CheckBoundary
{
    Write,
    Step,
    Wave,
    Apply,
    Run
}

/// <summary>
/// One declared check: a command whose exit code decides, bound to what a step changed (Plan Phase 3.1). Steps are made
/// per run, so a check cannot name one; it names the paths it is about instead.
/// </summary>
/// <param name="Id">A slug, unique in its scope; the file it is kept in is named by it. A workspace check shadows a global one of the same id.</param>
/// <param name="Description">What it checks, in a person's words - shown beside its result.</param>
/// <param name="Command">
/// What runs; its exit code decides. Null for a check that runs through the ecosystem (the implicit <c>build</c> and
/// <c>test</c>) - and for a manifest that only changes how a check it shadows is run (its severity, when it fires),
/// which keeps that check's command.
/// </param>
/// <param name="Matches">
/// Globs over workspace-relative paths (see <see cref="CheckGlob"/>): the check is owed when a change touches one.
/// Required for <see cref="CheckFire.Write"/> and <see cref="CheckFire.Step"/>; a <see cref="CheckFire.Run"/> check may
/// have none. Empty on a check that runs through an ecosystem means the paths that ecosystem owns (IEcosystem.Owns).
/// </param>
/// <param name="TimeoutSeconds">How long the command may run, within <see cref="CheckLimits"/>.</param>
/// <param name="Protects">Globs of files a step may not remove (Plan Phase 3.4); null when it protects none.</param>
public sealed record CheckDefinition(
    string Id,
    string? Description,
    string? Command,
    IReadOnlyList<string> Matches,
    CheckFire FireOn,
    CheckSeverity Severity,
    int TimeoutSeconds,
    IReadOnlyList<string>? Protects)
{
    /// <summary>Whether it runs at all. The UI turns a check off without deleting what it says (Plan Phase 3.5).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Value equality over the LISTS too. A record compares a list by reference, so the same check read twice was
    /// two different checks - and "the run still uses the definitions it started with" could not be asked.
    /// </summary>
    public bool Equals(CheckDefinition? other)
        => other is not null
           && Id == other.Id && Description == other.Description && Command == other.Command
           && FireOn == other.FireOn && Severity == other.Severity && TimeoutSeconds == other.TimeoutSeconds
           && Enabled == other.Enabled
           // Null-safe: a definition read from JSON that left a list out has null there, whatever the type says.
           && (Matches ?? []).SequenceEqual(other.Matches ?? [])
           && (Protects ?? []).SequenceEqual(other.Protects ?? []);

    public override int GetHashCode() => HashCode.Combine(Id, Command, FireOn, Severity, TimeoutSeconds, Enabled);
}

/// <summary>
/// A check as a run is given it: the definition with its gaps filled from what it shadows, where it came from, and what
/// it replaced - like a template's StoredTemplate. Shadowing is invisible in a merged list, and a person who edits the
/// global copy of a check their workspace overrides would watch the edit do nothing.
/// </summary>
/// <param name="Shadows">What this entry replaced; null when nothing of that id was below it.</param>
public sealed record ResolvedCheck(CheckDefinition Definition, CheckOrigin Origin, CheckOrigin? Shadows = null)
{
    /// <summary>
    /// The ecosystems whose own build or test this check is (IEcosystem.Name) - set on the implicit <c>build</c> and
    /// <c>test</c>, and kept by a manifest that raises them. Null for a check with a command of its own.
    /// </summary>
    public IReadOnlyList<string>? Ecosystems { get; init; }

    /// <summary>The manifest it was read from, as a full path; null for an implicit check nobody overrode.</summary>
    public string? File { get; init; }

    /// <summary>
    /// Whether a manifest of this check SAID its timeout, here or in a check it shadows - false when the timeout is the
    /// default for when it fires (<see cref="CheckLimits.DefaultTimeoutFor"/>). Kept so a manifest that raises a check
    /// to fire at another time without saying a timeout gets that time's default, not the default of the time the check
    /// below fired at. Only resolving reads it, so it is not stored with a run's snapshot.
    /// </summary>
    [JsonIgnore]
    public bool TimeoutSaid { get; init; }

    /// <summary>
    /// What the manifest this entry was read from SAID itself, field by field - null for the implicit build and test
    /// nobody overrode. The definition is that with what it shadows filled in; an editor needs the difference. Without it
    /// the UI guessed: a manifest that wrote out the same command as the global check was taken for a raise and saved
    /// without its command, and stopped running once the global check was deleted; a check turned off from a workspace
    /// list became a full copy of the global one, frozen. Only resolving reads it, so it is not stored with a snapshot.
    /// </summary>
    [JsonIgnore]
    public CheckManifestSaid? Said { get; init; }

    /// <summary>Its own manifest says a command - it is a check of its own, not a change to the one it shadows.</summary>
    [JsonIgnore]
    public bool CommandSaid => !string.IsNullOrWhiteSpace(Said?.Command);

    /// <summary>Runs through the ecosystem as the engine's build or test check does today, not through a command of its own.</summary>
    [JsonIgnore]
    public bool RunsThroughEcosystem => string.IsNullOrWhiteSpace(Definition.Command) && Ecosystems is { Count: > 0 };

    /// <summary>
    /// Whether it can be run at all: enabled, with a command or an ecosystem to run through. The one rule for "runnable" -
    /// a run's boundaries (WorkspaceChecks.Firing) and "Run checks now" (ChecksNow.Runnable) each wrote it out, and two
    /// copies of a rule drift apart.
    /// </summary>
    [JsonIgnore]
    public bool CanRun => Definition.Enabled && (RunsThroughEcosystem || !string.IsNullOrWhiteSpace(Definition.Command));

    [JsonIgnore]
    public string Id => Definition.Id;

    public bool Equals(ResolvedCheck? other)
        => other is not null && Definition.Equals(other.Definition) && Origin == other.Origin && Shadows == other.Shadows
           && File == other.File && (Ecosystems ?? []).SequenceEqual(other.Ecosystems ?? []);

    public override int GetHashCode() => HashCode.Combine(Definition, Origin, Shadows);
}

/// <summary>
/// A check manifest as written: every field optional, null where it says nothing - so a manifest that only changes how the
/// check below runs says only what it changes, and what it leaves unsaid follows that check (CheckStore.Resolve).
/// </summary>
public sealed record CheckManifestSaid(
    string? Description = null,
    string? Command = null,
    IReadOnlyList<string>? Matches = null,
    CheckFire? FireOn = null,
    CheckSeverity? Severity = null,
    int? TimeoutSeconds = null,
    IReadOnlyList<string>? Protects = null,
    bool? Enabled = null)
{
    /// <summary>A whole definition said in full - what a manifest written from it says.</summary>
    public static CheckManifestSaid All(CheckDefinition check)
        => new(check.Description, check.Command, check.Matches, check.FireOn, check.Severity, check.TimeoutSeconds,
            check.Protects, check.Enabled);
}

/// <summary>Why a manifest was skipped - its file and what is wrong with it, for the UI to show beside the file.</summary>
/// <param name="File">The manifest's full path.</param>
public sealed record CheckProblem(CheckScope Scope, string File, string Message);

/// <summary>What a manifest file hashed to, by its name in the checks folder - SHA-256, lower-case hex.</summary>
public sealed record CheckManifestHash(string File, string Sha256);

/// <summary>
/// A check the run owes and has not run yet, kept with the snapshot so a pause or a resume cannot lose it (Plan Phase
/// 3.2: a deferred blocking check is not a pass). Defined with the snapshot; filled where checks are run.
/// </summary>
/// <param name="CheckId">The check, by its id in the run's <see cref="CheckSnapshot"/>.</param>
/// <param name="DeferredTo">The boundary it is owed at.</param>
/// <param name="Reason">Why it has not run - said to a person as it is.</param>
public sealed record PendingCheck(string CheckId, CheckBoundary DeferredTo, string Reason)
{
    /// <summary>The step whose change it is owed for, when one is.</summary>
    public Guid? Step { get; init; }

    /// <summary>The wave it is owed at, when it was deferred to one.</summary>
    public int? Wave { get; init; }

    /// <summary>
    /// The protected files (Plan Phase 3.4) this obligation is about - ones a step removed or renamed while steps ran side
    /// by side, or proposed to remove in a staged run - to be looked for again at the boundary. Null: the check's command
    /// is what is owed. Kept apart from the command: a check that protects tests and also lints them owes both, and
    /// running its command would not say whether a test file is back.
    /// </summary>
    public IReadOnlyList<string>? Protected { get; init; }
}

/// <summary>
/// The checks a run is judged by, resolved ONCE when it is composed and carried whole - in the run's resources, its
/// checkpoint and its record (Plan Phase 3.1). Whole definitions and not ids or hashes: a manifest is a file anybody
/// can edit or delete, and a run read back later - or resumed - against the files as they are by then would be judged
/// by checks it was never given.
/// </summary>
/// <param name="Checks">Every resolved check, the disabled ones too - what was configured, not only what runs.</param>
/// <param name="Manifests">What each workspace manifest hashed to when the run was composed.</param>
public sealed record CheckSnapshot(IReadOnlyList<ResolvedCheck> Checks, IReadOnlyList<CheckManifestHash> Manifests)
{
    /// <summary>When it was taken.</summary>
    public DateTimeOffset TakenAt { get; init; }

    /// <summary>Why this workspace has no implicit build/test checks, or null when it has them - see CheckInventory.</summary>
    public string? NoImplicitChecks { get; init; }

    /// <summary>The manifests that were skipped, and why - so a run's record says what it was NOT given.</summary>
    public IReadOnlyList<CheckProblem> Problems { get; init; } = [];

    /// <summary>What was noted about manifests that were read (a write check read as advisory, a raise that does not apply).</summary>
    public IReadOnlyList<CheckProblem> Notes { get; init; } = [];

    /// <summary>A check of the snapshot by id, or null.</summary>
    public ResolvedCheck? Find(string id)
        => Checks.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The ids of the engine's own build and test of a workspace, as checks (Plan Phase 3.1) - here, beside the model, so the
/// store that lists them and the engine that runs them name them the same way.
/// </summary>
public static class ImplicitCheckIds
{
    public const string Build = "build";
    public const string Test = "test";
}

/// <summary>Where a workspace keeps its check manifests - one place, for the store that reads them and the guard that keeps them.</summary>
public static class CheckFolders
{
    /// <summary>&lt;workspace&gt;/.enactive/checks. Only its own <c>*.json</c> are manifests; what is kept below it is not.</summary>
    public static string WorkspaceFolderOf(string workspaceRoot) => Path.Combine(workspaceRoot, ".enactive", "checks");

    /// <summary>Whether a file in that folder is a manifest: a <c>.json</c> directly in it.</summary>
    public static bool IsManifest(string fileName) => fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What each manifest directly in a checks folder hashes to (SHA-256), by file name, ordered. Only the folder's own
    /// <c>*.json</c>: a check's detail output kept below it (Plan Phase 3.3) is not a manifest and judges nothing. A file
    /// that cannot be read is left out. Here, beside the folder, so the store that snapshots and the engine that compares
    /// on a resume hash the same way.
    /// </summary>
    public static IReadOnlyList<CheckManifestHash> HashManifests(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return [];
            return Directory.GetFiles(folder)
                .Where(f => IsManifest(Path.GetFileName(f)))
                .Select(f =>
                {
                    try { return new CheckManifestHash(Path.GetFileName(f), HashOf(File.ReadAllBytes(f))); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
                })
                .OfType<CheckManifestHash>()
                .OrderBy(h => h.File, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>SHA-256 of a manifest's bytes, lower-case hex.</summary>
    public static string HashOf(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
}

/// <summary>The limits a check is read within.</summary>
public static class CheckLimits
{
    /// <summary>
    /// What a manifest that says nothing gets. Two minutes: enough for a linter or a unit-test project, short enough that a
    /// forgotten timeout does not hold a step for long.
    /// </summary>
    public const int DefaultTimeoutSeconds = 120;

    public const int MinTimeoutSeconds = 1;

    /// <summary>
    /// An hour. A check runs inside a step, once per step it is owed for - a larger number is a typo (seconds written as
    /// milliseconds), and taking it at its word would hold every step that long before anybody noticed.
    /// </summary>
    public const int MaxTimeoutSeconds = 3600;

    /// <summary>Whether a timeout is one a check may have.</summary>
    public static bool IsSane(int seconds) => seconds is >= MinTimeoutSeconds and <= MaxTimeoutSeconds;

    /// <summary>
    /// The longest a check may take to be run at write time, in seconds. A write-time check runs INSIDE the worker's
    /// turn: the worker waits for it, and it can run every turn of an edit loop. Thirty seconds is a linter, a formatter
    /// check or a schema validation over a few files with room to spare, and caps what a turn can be held up per check;
    /// a build or a test run takes longer and belongs at the step, where it runs once. It is under the command tool's own
    /// limit (run_command stops a command at 60s), so a check's own timeout is what stops it. A write check declared
    /// with more is not run at write time - said once a run - rather than run and cut short: a check stopped half-way
    /// says nothing, every turn.
    /// </summary>
    public const int WriteTimeoutLimitSeconds = 30;

    /// <summary>
    /// What a check whose manifest says no timeout gets, by when it fires. A write check gets no more than
    /// <see cref="WriteTimeoutLimitSeconds"/>: given the general default (two minutes) it was over the write-time limit and
    /// never ran at write time, so a write check declared the obvious way - a command and the paths it is about - did
    /// nothing at all. A timeout a manifest DOES say is kept as said, and one over the limit is reported where it is skipped.
    /// </summary>
    public static int DefaultTimeoutFor(CheckFire fire)
        => fire == CheckFire.Write ? Math.Min(DefaultTimeoutSeconds, WriteTimeoutLimitSeconds) : DefaultTimeoutSeconds;
}

/// <summary>
/// The globs a check's <see cref="CheckDefinition.Matches"/> and <see cref="CheckDefinition.Protects"/> are written in,
/// over workspace-relative paths with '/' between folders.
///
/// <para><c>*</c> is any run of characters within one folder name, <c>**</c> any number of whole folders, <c>?</c> one
/// character, <c>[abc]</c>/<c>[!abc]</c> one of (not one of) a set, <c>{a,b}</c> either. A pattern with no '/' is matched
/// against the file NAME in any folder, as a .gitignore line is - <c>*.cs</c> means every C# file, not only those at the
/// root. A '\' is read as '/', so a pattern written on Windows means the same.</para>
///
/// <para>Case is ignored on Windows and macOS, whose file systems ignore it, and kept elsewhere: a check that missed
/// <c>Src/A.cs</c> because it said <c>src/**</c> would be silently not owed on the machine where the two are one file.</para>
///
/// <para><b>One parser for both questions.</b> <see cref="Problem"/> and <see cref="IsMatch"/> read a pattern through the
/// same translation, so a pattern with no problem is one that can be matched: when they were two readings, a comma in
/// a [set] inside {alternatives} passed the check and then threw out of the match.</para>
///
/// <para><b>Linear time.</b> The regex is built with <see cref="RegexOptions.NonBacktracking"/>, and repeated
/// <c>**/</c> are collapsed into one. A pattern is written by whoever writes a manifest, and is matched against every
/// path a step changed: a backtracking engine given <c>**/**/**/x</c> or <c>*a*a*a*b</c> over a deep path takes time
/// exponential in the pattern, inside a step.</para>
/// </summary>
public static class CheckGlob
{
    private static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    private static readonly RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant
                                                   | (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);

    /// <summary>
    /// Patterns already translated. A check's globs are asked about every changed path of every step, and building a
    /// non-backtracking regex is the expensive part. Bounded: patterns come from manifests, but a cache that only grows
    /// is a leak waiting for a long-lived service.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Regex? Regex, string? Problem)> Compiled = new();

    private const int CacheLimit = 512;

    /// <summary>What is wrong with a pattern, or null when it is one. A pattern with none never throws from <see cref="IsMatch"/>.</summary>
    public static string? Problem(string? pattern)
        => string.IsNullOrWhiteSpace(pattern) ? "a glob is empty" : Compile(pattern).Problem;

    /// <summary>Whether a workspace-relative path is one the pattern names. A pattern with a <see cref="Problem"/> names none.</summary>
    public static bool IsMatch(string pattern, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(pattern) || Compile(pattern).Regex is not { } regex)
            return false;
        var path = Normalize(relativePath).TrimStart('/');
        if (!Normalize(pattern).Contains('/'))
            path = path[(path.LastIndexOf('/') + 1)..];
        return regex.IsMatch(path);
    }

    private static string Normalize(string value) => value.Trim().Replace('\\', '/');

    private static (Regex? Regex, string? Problem) Compile(string pattern)
    {
        if (Compiled.TryGetValue(pattern, out var known))
            return known;
        if (Compiled.Count >= CacheLimit)
            Compiled.Clear();
        return Compiled[pattern] = Translate(pattern);
    }

    private static (Regex? Regex, string? Problem) Translate(string pattern)
    {
        var p = Normalize(pattern);
        // A path outside the workspace cannot be what a step changed: every changed path is workspace-relative.
        if (p.StartsWith('/') || (p.Length >= 2 && p[1] == ':'))
            return (null, $"'{pattern}' is not relative to the workspace");
        if (p.Split('/').Any(segment => segment == ".."))
            return (null, $"'{pattern}' leaves the workspace with '..'");

        var sb = new StringBuilder("^");
        var i = 0;
        if (Sequence(p, ref i, inBraces: false, sb) is { } wrong)
            return (null, $"'{pattern}' {wrong}");
        sb.Append('$');

        try
        {
            return (new Regex(sb.ToString(), Options), null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // What the parser lets through and a regex still refuses - a range written backwards, [z-a].
            return (null, $"'{pattern}' is not a glob that can be matched ({ex.Message})");
        }
    }

    /// <summary>
    /// Translates up to the end - or, inside {alternatives}, up to the ',' or '}' that ends this alternative, which is
    /// left for the caller. Returns what is wrong, or null.
    /// </summary>
    private static string? Sequence(string p, ref int i, bool inBraces, StringBuilder sb)
    {
        while (i < p.Length)
        {
            var c = p[i];
            switch (c)
            {
                case ',' or '}' when inBraces:
                    return null;
                case '}':
                    return "closes {alternatives} it never opened";
                case ']':
                    return "closes a [set] it never opened";
                case '*':
                    var stars = i;
                    while (i < p.Length && p[i] == '*') i++;
                    if (i - stars == 1)
                    {
                        sb.Append("[^/]*");
                        break;
                    }
                    if (i < p.Length && p[i] == '/')
                    {
                        // "**/" is any number of whole folders, none included - and "**/**/" is the same, said twice.
                        i++;
                        while (p.AsSpan(i).StartsWith("**/")) i += 3;
                        sb.Append("(?:[^/]*/)*");
                    }
                    else
                        sb.Append(".*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    i++;
                    break;
                case '[':
                    if (Set(p, ref i, sb) is { } badSet) return badSet;
                    break;
                case '{':
                    if (inBraces) return "nests {alternatives}, which is not supported";
                    i++;
                    sb.Append("(?:");
                    while (true)
                    {
                        if (Sequence(p, ref i, inBraces: true, sb) is { } inner) return inner;
                        if (i >= p.Length) return "opens {alternatives} it never closes";
                        if (p[i] == ',')
                        {
                            sb.Append('|');
                            i++;
                            continue;
                        }
                        i++; // the '}'
                        break;
                    }
                    sb.Append(')');
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }
        return null;
    }

    /// <summary>
    /// A [set] from its '[' to its ']'. A ']' first in it (after the '!', if any) is one of its characters, as in a shell;
    /// everything in it is literal except a range's '-'.
    /// </summary>
    private static string? Set(string p, ref int i, StringBuilder sb)
    {
        var j = i + 1;
        var negated = j < p.Length && p[j] == '!';
        if (negated) j++;
        var first = j;
        if (j < p.Length && p[j] == ']') j++;
        var close = p.IndexOf(']', j);
        if (close < 0) return "opens a [set] it never closes";

        sb.Append('[');
        // Negated, it still names one character of a name, as '?' does - never the '/' between folders: "[^x]" alone let
        // "a[!b]c/*.txt" match a/c/d.txt, a file one folder up from where the glob says.
        if (negated) sb.Append("^/");
        foreach (var ch in p.AsSpan(first, close - first))
        {
            if (ch is '\\' or ']' or '[' or '^') sb.Append('\\');
            sb.Append(ch);
        }
        sb.Append(']');
        i = close + 1;
        return null;
    }
}
