namespace Enactive.Workspace;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Enactive.Core.Builds;
using Enactive.Core.Checks;
using Enactive.Core.Templates;

/// <summary>
/// The checks visible in one workspace, and what was wrong with the files that are not among them - what the UI lists
/// (Plan Phase 3.5) and what a run's <see cref="CheckSnapshot"/> is made from.
/// </summary>
/// <param name="Checks">Every check, workspace ones shadowing global ones shadowing implicit ones, ordered by id.</param>
/// <param name="Problems">The manifests that were skipped, with their files.</param>
/// <param name="NoImplicitChecks">
/// Why there is no implicit <c>build</c>/<c>test</c> here, or null when there are: said in the UI rather than shown as
/// empty ids. The implicit checks are the engine's own build and test of an ecosystem it knows, and the only one today
/// is .NET - a Node or Python workspace declares its commands.
/// </param>
public sealed record CheckInventory(
    IReadOnlyList<ResolvedCheck> Checks, IReadOnlyList<CheckProblem> Problems, string? NoImplicitChecks);

/// <summary>
/// Check manifests as files, one check per file named by its id (Plan Phase 3.1), modelled on <see cref="TemplateStore"/>
/// and for the same reasons: a check that lives IN the repository is versioned with the code it checks, arrives with a
/// clone and is reviewed in a pull request.
///
/// <para><b>Layers.</b> The implicit <c>build</c> and <c>test</c> of an ecosystem the workspace is; then the global
/// folder; then the workspace's <c>.enactive/checks</c>. Each shadows the one below by id. A manifest with no command
/// does not replace the check it shadows - it changes how that one runs (its severity, when it fires) and keeps its
/// command: that is how a workspace raises the implicit <c>build</c> to blocking without redeclaring how .NET builds.</para>
///
/// <para><b>Never an exception out of a listing.</b> A file that cannot be read or is not a valid check is reported, with
/// its file, and skipped. One bad manifest must not hide the others, and a run must not fail to start over a typo in a
/// file the run does not even need.</para>
/// </summary>
public sealed class CheckStore
{
    /// <summary>The implicit ids, in the order they are listed.</summary>
    public const string BuildId = "build";
    public const string TestId = "test";

    /// <summary>The workspace's checks folder, relative to its root.</summary>
    public const string WorkspaceRelativeFolder = ".enactive/checks";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Names only: a number would be read as whatever member has that position, and "fireOn": 7 is no member at all.
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private readonly string? _workspaceRoot;
    private readonly IReadOnlyList<IEcosystem> _ecosystems;

    /// <param name="globalFolder">
    /// Overrides where the global checks live. A test passes this: without it, it would read the real %APPDATA% folder
    /// and its result would depend on the checks of whoever runs it.
    /// </param>
    /// <param name="ecosystems">The kinds of project whose build and test become implicit checks (KnownEcosystems.All); none by default.</param>
    public CheckStore(string? workspaceRoot = null, string? globalFolder = null, IReadOnlyList<IEcosystem>? ecosystems = null)
    {
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? null : workspaceRoot;
        GlobalFolder = string.IsNullOrWhiteSpace(globalFolder) ? DefaultGlobalFolder : globalFolder!;
        _ecosystems = ecosystems ?? [];
    }

    /// <summary>Next to settings.json and the global templates: %APPDATA%/Enactive/checks.</summary>
    public static string DefaultGlobalFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Enactive", "checks");

    public string GlobalFolder { get; }

    public string? WorkspaceFolder => _workspaceRoot is null ? null : WorkspaceFolderOf(_workspaceRoot);

    /// <summary>Where a workspace keeps its checks.</summary>
    public static string WorkspaceFolderOf(string workspaceRoot) => CheckFolders.WorkspaceFolderOf(workspaceRoot);

    /// <summary>Every check visible here, and every manifest that was skipped and why.</summary>
    public CheckInventory Inventory()
    {
        var problems = new List<CheckProblem>();
        var byId = new Dictionary<string, ResolvedCheck>(StringComparer.OrdinalIgnoreCase);

        var (implicitChecks, noImplicit) = Implicit();
        foreach (var check in implicitChecks)
            byId[check.Id] = check;

        foreach (var (scope, folder) in Folders())
            foreach (var manifest in ReadFolder(scope, folder, problems))
            {
                byId.TryGetValue(manifest.Id, out var below);
                if (Resolve(manifest, scope, below, noImplicit) is { } resolved)
                    byId[manifest.Id] = resolved;
                else
                    problems.Add(new CheckProblem(scope, manifest.File, ProblemWithoutCommand(manifest.Id, below, noImplicit)));
            }

        return new CheckInventory(
            byId.Values.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
            problems.OrderBy(p => p.File, StringComparer.OrdinalIgnoreCase).ToArray(),
            noImplicit);
    }

    /// <summary>
    /// What a run is given: the inventory, and what each workspace manifest hashed to now. Taken once, when the run is
    /// composed (RunComposer); the run uses nothing else.
    /// </summary>
    public CheckSnapshot Snapshot()
    {
        var inventory = Inventory();
        return new CheckSnapshot(inventory.Checks, WorkspaceFolder is { } folder ? HashManifests(folder) : [])
        {
            TakenAt = DateTimeOffset.UtcNow,
            NoImplicitChecks = inventory.NoImplicitChecks,
            Problems = inventory.Problems
        };
    }

    /// <summary>
    /// What each manifest directly in a checks folder hashes to (SHA-256), by file name. Only the folder's own
    /// <c>*.json</c>: a check's detail output kept below it (Plan Phase 3.3) is not a manifest and judges nothing.
    /// </summary>
    public static IReadOnlyList<CheckManifestHash> HashManifests(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return [];
            return Directory.GetFiles(folder, "*.json")
                .Select(f =>
                {
                    try { return new CheckManifestHash(Path.GetFileName(f), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)))); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
                })
                .OfType<CheckManifestHash>()
                .OrderBy(h => h.File, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// What is wrong with a check before it is saved to <paramref name="scope"/> - the same rules a manifest is read by,
    /// so the UI refuses what the store would skip. Empty when it may be saved.
    /// </summary>
    public IReadOnlyList<string> Validate(CheckDefinition check, CheckScope scope)
    {
        var problems = Intrinsic(ManifestOf(check, file: PathOrName(check.Id, scope)));
        if (problems.Count > 0) return problems;

        // A check without a command changes one below it; there must be one to change.
        if (string.IsNullOrWhiteSpace(check.Command))
        {
            var inventory = Inventory();
            var below = Below(check.Id, scope, inventory);
            if (below is null || Resolve(ManifestOf(check, PathOrName(check.Id, scope)), scope, below, inventory.NoImplicitChecks) is null)
                return [ProblemWithoutCommand(check.Id, below, inventory.NoImplicitChecks)];
        }
        return [];
    }

    /// <summary>
    /// Writes a check to its file, <c>&lt;id&gt;.json</c> in <paramref name="scope"/>, refusing one the store would skip -
    /// a refusal here is an exception, because it is somebody's explicit save, not a listing.
    /// </summary>
    public void Save(CheckDefinition check, CheckScope scope)
    {
        if (Validate(check, scope) is { Count: > 0 } problems)
            throw new ArgumentException("This check cannot be stored: " + string.Join("; ", problems), nameof(check));

        var path = PathFor(check.Id, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicWrite.Replace(path, ToJson(check));
    }

    /// <summary>
    /// Removes a check from one scope: its file, and any other manifest there that declares the same id (a file named
    /// otherwise by hand). A check that is not there is not an error - it is the goal.
    /// </summary>
    public void Delete(string id, CheckScope scope)
    {
        var path = PathFor(id, scope);
        if (File.Exists(path)) File.Delete(path);

        var folder = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder, "*.json"))
            if (Read(file, out _) is { } manifest && string.Equals(manifest.Id, id, StringComparison.OrdinalIgnoreCase))
                File.Delete(file);
    }

    /// <summary>The file a check with this id lives in. Refuses an id that is not a safe slug - it becomes a file name.</summary>
    public string PathFor(string id, CheckScope scope)
    {
        if (!TemplateValidator.IsSafeId(id))
            throw new ArgumentException(
                $"'{id}' is not a valid check id: lower-case letters, digits, '-' and '_', starting with a letter. It "
                + "becomes a file name, so anything else is refused rather than sanitised.", nameof(id));

        var folder = scope == CheckScope.Workspace
            ? WorkspaceFolder ?? throw new InvalidOperationException(
                "This store has no workspace, so it cannot address a workspace check.")
            : GlobalFolder;
        return Path.Combine(folder, id + ".json");
    }

    /// <summary>A check as its file holds it.</summary>
    public static string ToJson(CheckDefinition check) => JsonSerializer.Serialize(check, Json);

    // ── reading ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A manifest as written, every field optional - so a manifest that only raises a check can say only what it
    /// changes, and "not said" is told apart from a default.
    /// </summary>
    private sealed class ManifestFile
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
        public string? Command { get; set; }
        public List<string>? Matches { get; set; }
        public CheckFire? FireOn { get; set; }
        public CheckSeverity? Severity { get; set; }
        public int? TimeoutSeconds { get; set; }
        public List<string>? Protects { get; set; }
        public bool? Enabled { get; set; }
    }

    private sealed record Manifest(string Id, string File, ManifestFile Said);

    private IEnumerable<(CheckScope Scope, string Folder)> Folders()
    {
        yield return (CheckScope.Global, GlobalFolder);
        if (WorkspaceFolder is { } local) yield return (CheckScope.Workspace, local);
    }

    private static IEnumerable<Manifest> ReadFolder(CheckScope scope, string folder, List<CheckProblem> problems)
    {
        string[] files;
        try { files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new CheckProblem(scope, folder, "The folder cannot be read: " + ex.Message));
            return [];
        }

        var read = new List<Manifest>();
        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (Read(file, out var unreadable) is not { } manifest)
            {
                problems.Add(new CheckProblem(scope, file, unreadable!));
                continue;
            }
            if (Intrinsic(manifest) is { Count: > 0 } wrong)
            {
                problems.Add(new CheckProblem(scope, file, string.Join("; ", wrong)));
                continue;
            }
            read.Add(manifest);
        }

        // Two files of one scope saying the same id: neither is taken. Picking one by file order would make which check
        // judges the run an accident of naming, and the other file would look as if it were in force.
        foreach (var twins in read.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var twin in twins)
                problems.Add(new CheckProblem(scope, twin.File,
                    $"The id '{twin.Id}' is also declared by "
                    + string.Join(", ", twins.Where(t => t != twin).Select(t => Path.GetFileName(t.File)))
                    + "; an id must be unique in its scope, so none of them is used."));

        return read.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1).Select(g => g.Single());
    }

    private static Manifest? Read(string file, out string? problem)
    {
        problem = null;
        ManifestFile? said;
        try
        {
            said = JsonSerializer.Deserialize<ManifestFile>(File.ReadAllText(file), Json);
        }
        catch (JsonException ex)
        {
            problem = "It is not a check manifest as JSON: " + ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "It cannot be read: " + ex.Message;
            return null;
        }
        if (said is null)
        {
            problem = "It is empty.";
            return null;
        }

        // The id in the file, or the file's name when it says none: one check per file, named by it.
        var id = string.IsNullOrWhiteSpace(said.Id) ? Path.GetFileNameWithoutExtension(file) : said.Id.Trim();
        return new Manifest(id, file, said);
    }

    /// <summary>What is wrong with a manifest on its own, before what it shadows is known.</summary>
    private static List<string> Intrinsic(Manifest manifest)
    {
        var problems = new List<string>();
        var said = manifest.Said;

        if (!TemplateValidator.IsSafeId(manifest.Id))
            problems.Add($"'{manifest.Id}' is not a valid check id: lower-case letters, digits, '-' and '_', starting with a letter");

        // Refused, not clamped: a check that ran under a timeout nobody wrote would fail - or hold a step - for a reason
        // its manifest does not show. Out of range is almost always a unit slip (milliseconds for seconds).
        if (said.TimeoutSeconds is { } timeout && !CheckLimits.IsSane(timeout))
            problems.Add($"timeoutSeconds {timeout} is outside {CheckLimits.MinTimeoutSeconds}-{CheckLimits.MaxTimeoutSeconds}");

        foreach (var glob in (said.Matches ?? []).Concat(said.Protects ?? []))
            if (CheckGlob.Problem(glob) is { } bad)
                problems.Add(bad);

        // A manifest WITH a command stands on its own, so it must be complete here. One without is read against what it
        // shadows (Resolve), which may supply the paths.
        if (!string.IsNullOrWhiteSpace(said.Command)
            && (said.FireOn ?? DefaultFire) is CheckFire.Write or CheckFire.Step
            && said.Matches is not { Count: > 0 })
            problems.Add($"a check that fires on {(said.FireOn ?? DefaultFire).ToString().ToLowerInvariant()} must say which paths it is about (matches)");

        return problems;
    }

    /// <summary>
    /// When a manifest says nothing about it, a check fires after a step that touched its paths - the common case, and
    /// the one a person writing "check X when Y changes" means. Advisory unless it says otherwise: blocking is opted into.
    /// </summary>
    private const CheckFire DefaultFire = CheckFire.Step;

    /// <summary>
    /// The check a manifest makes, over what it shadows (null when nothing of its id is below). Null when it has no
    /// command and nothing below gives it one.
    /// </summary>
    private static ResolvedCheck? Resolve(Manifest manifest, CheckScope scope, ResolvedCheck? below, string? noImplicit)
    {
        var said = manifest.Said;
        var origin = scope == CheckScope.Workspace ? CheckOrigin.Workspace : CheckOrigin.Global;

        if (!string.IsNullOrWhiteSpace(said.Command))
            return new ResolvedCheck(
                new CheckDefinition(manifest.Id, said.Description?.Trim(), said.Command.Trim(), said.Matches?.ToArray() ?? [],
                    said.FireOn ?? DefaultFire, said.Severity ?? CheckSeverity.Advisory,
                    said.TimeoutSeconds ?? CheckLimits.DefaultTimeoutSeconds, said.Protects?.ToArray())
                { Enabled = said.Enabled ?? true },
                origin, below?.Origin)
            { File = manifest.File };

        // No command: it changes how the check below runs and keeps that check's command - or, for the implicit build
        // and test, its ecosystem. What it does not say is the check below's.
        if (below is null) return null;
        var was = below.Definition;
        var matches = said.Matches is { Count: > 0 } narrowed ? narrowed.ToArray() : was.Matches;
        var fire = said.FireOn ?? was.FireOn;

        if (fire is CheckFire.Write or CheckFire.Step && matches.Count == 0 && !below.RunsThroughEcosystem)
            return null;

        // Raising the implicit build or test to Blocking, or to fire per step, REVERSES an explicit decision: "the
        // engine's own checks never decide a run", because a new build error at the end does not prove this run made it
        // - the workspace can change around a run (RunOutcomeDecision.Settle, run 3fe4f8, commit 547efc8). So it stays
        // opt-in per check: only a manifest of that id that says so raises it, here, and nothing raises it by default.
        var severity = said.Severity ?? was.Severity;

        return below with
        {
            Definition = was with
            {
                Description = string.IsNullOrWhiteSpace(said.Description) ? was.Description : said.Description.Trim(),
                Matches = matches,
                FireOn = fire,
                Severity = severity,
                TimeoutSeconds = said.TimeoutSeconds ?? was.TimeoutSeconds,
                Protects = said.Protects?.ToArray() ?? was.Protects,
                Enabled = said.Enabled ?? was.Enabled
            },
            Origin = origin,
            Shadows = below.Origin,
            File = manifest.File
        };
    }

    private static string ProblemWithoutCommand(string id, ResolvedCheck? below, string? noImplicit)
        => below is not null
            ? $"'{id}' has no command, so it changes the {below.Origin.ToString().ToLowerInvariant()} check it shadows - and "
              + "that one has no command of its own either, so it must say which paths it is about (matches)"
            : id is BuildId or TestId && noImplicit is not null
                ? $"'{id}' has no command, and there is no implicit '{id}' check to raise here: {noImplicit} Declare its command."
                : $"'{id}' has no command, and there is no check of that id below it to change - declare its command";

    private static Manifest ManifestOf(CheckDefinition check, string file)
        => new(check.Id, file, new ManifestFile
        {
            Id = check.Id, Description = check.Description, Command = check.Command, Matches = check.Matches?.ToList(),
            FireOn = check.FireOn, Severity = check.Severity, TimeoutSeconds = check.TimeoutSeconds,
            Protects = check.Protects?.ToList(), Enabled = check.Enabled
        });

    private string PathOrName(string id, CheckScope scope)
    {
        try { return PathFor(id, scope); }
        catch (ArgumentException) { return id + ".json"; }
    }

    /// <summary>What a check of this id saved to <paramref name="scope"/> would shadow.</summary>
    private ResolvedCheck? Below(string id, CheckScope scope, CheckInventory inventory)
    {
        var (implicitChecks, _) = Implicit();
        var below = implicitChecks.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (scope == CheckScope.Global) return below;

        // Under a workspace check: the global one, as the global layer resolved it over the implicit one.
        var problems = new List<CheckProblem>();
        var global = ReadFolder(CheckScope.Global, GlobalFolder, problems)
            .FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        return global is null ? below : Resolve(global, CheckScope.Global, below, inventory.NoImplicitChecks) ?? below;
    }

    // ── implicit ────────────────────────────────────────────────────────────

    /// <summary>
    /// The engine's own build and test of the ecosystems this workspace is, as checks: advisory, with the run's final
    /// checks, and no command of their own - they run through the ecosystem as the engine's build check does today.
    /// Where no ecosystem applies there are none, and the reason says so.
    /// </summary>
    private (IReadOnlyList<ResolvedCheck> Checks, string? NoImplicit) Implicit()
    {
        if (_workspaceRoot is null)
            return ([], "Implicit build and test checks belong to a workspace, and this list has none.");

        var detected = new List<EcosystemTargets>();
        foreach (var ecosystem in _ecosystems)
        {
            try { if (ecosystem.Detect(_workspaceRoot) is { } targets) detected.Add(targets); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not detected */ }
        }

        if (detected.Count == 0)
            return ([], _ecosystems.Count == 0
                ? "The engine knows no ecosystem to build or test here."
                : $"This workspace is not one the engine can build or test itself (it knows {string.Join(", ", _ecosystems.Select(e => e.Name))}), "
                  + "so there are no implicit build and test checks; declare their commands as checks of your own.");

        var checks = new List<ResolvedCheck>();
        var building = detected.Where(t => t.Build.Count > 0).Select(t => t.Ecosystem).ToArray();
        if (building.Length > 0)
            checks.Add(ImplicitCheck(BuildId, $"The workspace builds ({string.Join(", ", building)}), as the engine's own build check.", building));
        var testing = detected.Where(t => t.Tests.Count > 0).Select(t => t.Ecosystem).ToArray();
        if (testing.Length > 0)
            checks.Add(ImplicitCheck(TestId, $"The workspace's tests pass ({string.Join(", ", testing)}), as the engine's own test check.", testing));
        return (checks, null);
    }

    private static ResolvedCheck ImplicitCheck(string id, string description, IReadOnlyList<string> ecosystems)
        => new(new CheckDefinition(id, description, Command: null, Matches: [], CheckFire.Run, CheckSeverity.Advisory,
                CheckLimits.DefaultTimeoutSeconds, Protects: null), CheckOrigin.Implicit)
        { Ecosystems = ecosystems };
}
