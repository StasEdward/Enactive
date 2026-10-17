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

    /// <summary>Runs through the ecosystem as the engine's build or test check does today, not through a command of its own.</summary>
    [JsonIgnore]
    public bool RunsThroughEcosystem => string.IsNullOrWhiteSpace(Definition.Command) && Ecosystems is { Count: > 0 };

    [JsonIgnore]
    public string Id => Definition.Id;

    public bool Equals(ResolvedCheck? other)
        => other is not null && Definition.Equals(other.Definition) && Origin == other.Origin && Shadows == other.Shadows
           && File == other.File && (Ecosystems ?? []).SequenceEqual(other.Ecosystems ?? []);

    public override int GetHashCode() => HashCode.Combine(Definition, Origin, Shadows);
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

    /// <summary>A check of the snapshot by id, or null.</summary>
    public ResolvedCheck? Find(string id)
        => Checks.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Where a workspace keeps its check manifests - one place, for the store that reads them and the guard that keeps them.</summary>
public static class CheckFolders
{
    /// <summary>&lt;workspace&gt;/.enactive/checks. Only its own <c>*.json</c> are manifests; what is kept below it is not.</summary>
    public static string WorkspaceFolderOf(string workspaceRoot) => Path.Combine(workspaceRoot, ".enactive", "checks");

    /// <summary>Whether a file in that folder is a manifest: a <c>.json</c> directly in it.</summary>
    public static bool IsManifest(string fileName) => fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
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
/// </summary>
public static class CheckGlob
{
    private static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>What is wrong with a pattern, or null when it is one.</summary>
    public static string? Problem(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "a glob is empty";
        var p = Normalize(pattern);
        // A path outside the workspace cannot be what a step changed: every changed path is workspace-relative.
        if (p.StartsWith('/') || (p.Length >= 2 && p[1] == ':'))
            return $"'{pattern}' is not relative to the workspace";
        if (p.Split('/').Any(segment => segment == ".."))
            return $"'{pattern}' leaves the workspace with '..'";

        var inSet = false;
        var inBraces = false;
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (inSet)
            {
                if (c == ']' && p[i - 1] != '[' && !(p[i - 1] == '!' && p[i - 2] == '[')) inSet = false;
                continue;
            }
            switch (c)
            {
                case '[': inSet = true; break;
                case ']': return $"'{pattern}' closes a [set] it never opened";
                case '{' when inBraces: return $"'{pattern}' nests {{alternatives}}, which is not supported";
                case '{': inBraces = true; break;
                case '}' when !inBraces: return $"'{pattern}' closes {{alternatives}} it never opened";
                case '}': inBraces = false; break;
            }
        }
        if (inSet) return $"'{pattern}' opens a [set] it never closes";
        if (inBraces) return $"'{pattern}' opens {{alternatives}} it never closes";
        return null;
    }

    /// <summary>Whether a workspace-relative path is one the pattern names. A pattern with a <see cref="Problem"/> names none.</summary>
    public static bool IsMatch(string pattern, string relativePath)
    {
        if (Problem(pattern) is not null) return false;
        var p = Normalize(pattern);
        var path = Normalize(relativePath).TrimStart('/');
        if (!p.Contains('/'))
            path = path[(path.LastIndexOf('/') + 1)..];
        return Regex.IsMatch(path, ToRegex(p),
            IgnoreCase ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant);
    }

    private static string Normalize(string value) => value.Trim().Replace('\\', '/');

    private static string ToRegex(string p)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            switch (c)
            {
                case '*' when i + 1 < p.Length && p[i + 1] == '*':
                    // "**/" is any number of whole folders, none included; "/**" at the end is everything below.
                    var slashAfter = i + 2 < p.Length && p[i + 2] == '/';
                    sb.Append(slashAfter ? "(?:[^/]*/)*" : ".*");
                    i += slashAfter ? 2 : 1;
                    break;
                case '*': sb.Append("[^/]*"); break;
                case '?': sb.Append("[^/]"); break;
                case '[':
                    var close = p.IndexOf(']', i + 2);
                    var body = p[(i + 1)..close];
                    sb.Append('[').Append(body.StartsWith('!') ? "^" + Regex.Escape(body[1..]) : Regex.Escape(body)).Append(']');
                    i = close;
                    break;
                case '{':
                    var end = p.IndexOf('}', i);
                    sb.Append("(?:").Append(string.Join('|', p[(i + 1)..end].Split(',').Select(ToRegexPart))).Append(')');
                    i = end;
                    break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return sb.Append('$').ToString();
    }

    private static string ToRegexPart(string alternative) => ToRegex(alternative)[1..^1];
}
