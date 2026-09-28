namespace Enactive.Agents;

using Enactive.Core.Builds;
using Enactive.Core.Execution;
using Enactive.Core.Templates;

/// <summary>One step that ended in a wave, and what it changed as the engine recorded it.</summary>
/// <param name="Wrote">Workspace-relative paths, forward slashes: the store's record and the journal's.</param>
/// <param name="UnrecordedWrites">It ran a call that can change the workspace and recorded no paths - a command.</param>
internal sealed record WaveStep(Guid Id, int No, string Title, IReadOnlyList<string> Wrote, bool UnrecordedWrites)
{
    public string Name => $"step {No} (\"{Title}\")";

    /// <summary>Whether this step can have changed what an ecosystem's build reads (Phase 6.1).</summary>
    public bool Changes(IEcosystem ecosystem) => UnrecordedWrites || Wrote.Any(ecosystem.Owns);

    public static WaveStep Of(Guid id, int no, string title, IEnumerable<string> touched,
        IEnumerable<ExecutedAction> actions, string root)
    {
        var wrote = touched.Concat(actions.Where(a => a.Outcome != ActionOutcome.Refused && a.ChangedPaths is { Count: > 0 })
                .SelectMany(a => a.ChangedPaths!))
            .Select(p => WaveCapture.Relative(p, root)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var unrecorded = actions.Any(a => a.Outcome != ActionOutcome.Refused
                                          && a.WorkspaceEffect != Enactive.Core.Tools.WorkspaceEffect.None
                                          && a.ChangedPaths is not { Count: > 0 });
        return new(id, no, title, wrote, unrecorded);
    }
}

/// <summary>
/// The content of every file an ecosystem's build reads, at one moment - what a wave's steps are
/// taken back to when one of them has to be tried on its own (Phase 6.3). Read off the disk, so it
/// is the same in a git work tree and outside one; kept only up to a size, and a capture that would
/// be larger says so instead of being taken.
/// </summary>
internal sealed class WaveCapture
{
    /// <summary>Folders never read: build output, the engine's own, version control, packages.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".enactive", "bin", "obj", "node_modules", ".vs", ".idea" };

    internal const long MaxBytes = 64L * 1024 * 1024;

    private WaveCapture(IReadOnlyDictionary<string, byte[]>? files, string? notTaken)
        => (Files, NotTaken) = (files, notTaken);

    /// <summary>Path to bytes, or null when the capture was not taken - see <see cref="NotTaken"/>.</summary>
    public IReadOnlyDictionary<string, byte[]>? Files { get; }

    public string? NotTaken { get; }

    public static WaveCapture Take(string root, IReadOnlyList<IEcosystem> ecosystems, long maxBytes = MaxBytes)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        try
        {
            var pending = new Stack<string>([root]);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    if (!Skipped.Contains(Path.GetFileName(sub)))
                        pending.Push(sub);
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (!ecosystems.Any(e => e.Owns(rel))) continue;
                    var bytes = File.ReadAllBytes(file);
                    total += bytes.Length;
                    if (total > maxBytes)
                        return new(null, $"what the build reads is more than {maxBytes / (1024 * 1024)} MB, too much to keep a copy of");
                    files[rel] = bytes;
                }
            }
            return new(files, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, "the files the build reads could not all be read: " + ex.Message);
        }
    }

    /// <summary>The paths whose content differs between two captures, added and removed ones included.</summary>
    public static IReadOnlySet<string> Changed(WaveCapture before, WaveCapture after)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, bytes) in after.Files!)
            if (!before.Files!.TryGetValue(path, out var was) || !was.AsSpan().SequenceEqual(bytes))
                changed.Add(path);
        foreach (var path in before.Files!.Keys)
            if (!after.Files.ContainsKey(path))
                changed.Add(path);
        return changed;
    }

    /// <summary>
    /// Puts each of <paramref name="paths"/> as <paramref name="source"/> has it - removing it where the
    /// source had no such file. The engine's own writes, made only when no step is running.
    /// </summary>
    public static void Put(string root, IEnumerable<string> paths, WaveCapture source)
    {
        foreach (var path in paths)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            if (source.Files!.TryGetValue(path, out var bytes))
            {
                if (Path.GetDirectoryName(full) is { } dir) Directory.CreateDirectory(dir);
                File.WriteAllBytes(full, bytes);
            }
            else if (File.Exists(full))
                File.Delete(full);
        }
    }

    /// <summary>A workspace-relative path with forward slashes, or null for one outside the workspace.</summary>
    public static string? Relative(string path, string root)
    {
        var rel = Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path;
        rel = rel.Replace('\\', '/');
        while (rel.StartsWith("./", StringComparison.Ordinal)) rel = rel[2..];
        return rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? null : rel;
    }
}

/// <summary>
/// What one wave broke, as something a trial build can be asked about: the new errors by identity and
/// count, the tests that passed and fail, or a build that passed and fails with nothing readable.
/// </summary>
internal sealed record WaveRegression(BuildBaseline Reference, IReadOnlyList<DiagnosticRegression> Errors,
    IReadOnlyList<string> Tests, bool Broke)
{
    /// <summary>What regressed in <paramref name="after"/> against <paramref name="reference"/>, or null for nothing.</summary>
    public static WaveRegression? Of(BuildBaseline reference, CriterionResult after, string root)
    {
        if (!reference.Taken || after.ExitCode is not { } exit) return null;
        if (reference.Kind == BaselineKind.Test)
        {
            var tests = reference.Ecosystem.ParseTests(after.Output ?? "");
            if (tests is null)
                return reference.ExitCode == 0 ? new(reference, [], [], true) : null;
            var broken = tests.Regressions(reference.Tests!);
            return broken.Count > 0 ? new(reference, [], broken, false) : null;
        }
        var now = DiagnosticSet.Of(reference.Ecosystem.ParseDiagnostics(after.Output ?? "", root));
        var added = now.NewSince(reference.Diagnostics!).Where(r => !reference.Ecosystem.IsEnvironmental(r.Identity)).ToArray();
        if (added.Length > 0) return new(reference, added, [], false);
        return reference.ExitCode == 0 && exit != 0 && now.NewSince(reference.Diagnostics!).Count == 0
            ? new(reference, [], [], true) : null;
    }

    /// <summary>What regressed in both of two runs - a test that failed only once is flaky, not the wave's.</summary>
    public static WaveRegression? Of(BuildBaseline reference, CriterionResult first, CriterionResult? again, string root)
    {
        var one = Of(reference, first, root);
        if (one is null || again is null || reference.Kind != BaselineKind.Test) return one;
        var two = Of(reference, again, root);
        if (two is null) return null;
        var tests = one.Tests.Where(two.Tests.Contains).ToArray();
        return tests.Length == 0 && !(one.Broke && two.Broke) ? null : one with { Tests = tests, Broke = one.Broke && two.Broke };
    }

    /// <summary>
    /// Whether a trial's result shows ANY of this regression again: true, false, or null when the trial
    /// could not say (the command did not run).
    /// </summary>
    public bool? ReproducedBy(CriterionResult trial, string root)
    {
        if (trial.ExitCode is not { } exit) return null;
        if (Reference.Kind == BaselineKind.Test)
        {
            var tests = Reference.Ecosystem.ParseTests(trial.Output ?? "");
            if (tests is null) return Broke ? true : null;
            var failing = tests.Regressions(Reference.Tests!);
            return Broke ? failing.Count > 0 : Tests.Any(failing.Contains);
        }
        var now = DiagnosticSet.Of(Reference.Ecosystem.ParseDiagnostics(trial.Output ?? "", root));
        return Broke
            ? exit != 0
            : Errors.Any(e => now.Count(e.Identity) > Reference.Diagnostics!.Count(e.Identity));
    }

    /// <summary>What regressed, briefly: the first errors or tests by name.</summary>
    public string Describe()
        => Broke ? $"{Reference.Ecosystem.Name} {(Reference.Kind == BaselineKind.Test ? "tests" : "build")} of {Reference.Target} no longer passes"
            : Errors.Count > 0
                ? string.Join("; ", Errors.Take(3).Select(e => $"{e.Identity.Code} in {e.Identity.Path ?? "(no file)"}"))
                  + (Errors.Count > 3 ? $"; and {Errors.Count - 3} more" : "")
                : string.Join(", ", Tests.Take(3)) + (Tests.Count > 3 ? $", and {Tests.Count - 3} more" : "");
}

/// <summary>Who a wave's regression belongs to: one step, or nobody the engine can name - said as such.</summary>
/// <param name="Culprit">The step whose changes alone reproduce it, when exactly one does.</param>
/// <param name="Suspects">The steps it could be, when it is not one.</param>
internal sealed record Attribution(WaveStep? Culprit, IReadOnlyList<WaveStep> Suspects, string Explanation, int Trials)
{
    public bool Ambiguous => Culprit is null;
}

/// <summary>
/// Phase 6: a wave's steps are validated once, together, where the plan has nothing running; and a
/// regression found there is attributed to the step that caused it, or reported as ambiguous.
///
/// <para><b>What a wave is.</b> The steps that ended since the last moment nothing was running. At one
/// step at a time every step is its own wave. With parallel steps, the steps that overlapped are one:
/// validating between two of them would build a workspace another step is still writing.</para>
///
/// <para><b>What needs validation (6.1).</b> A wave whose steps only read, or wrote only files no
/// ecosystem's build reads, needs none. A wave with a step that wrote such a file - or ran a command
/// whose writes nobody recorded - is marked as needing it, and is built and tested once at its end.</para>
///
/// <para><b>Attribution (6.3).</b> When the wave broke something, each step's changes are tried on their
/// own: the files the wave changed are put back as they were before it, the step's are laid over them,
/// and the same build is run again. One step whose changes alone reproduce the regression is the cause;
/// anything else - none, several, a file two steps both wrote, changes no step recorded - is said to be
/// ambiguous, and why. The workspace is always put back as the wave left it. A wave with one candidate
/// needs no trial: nothing else it did can change what the build reads.</para>
///
/// <para><b>Against what.</b> Each wave is compared with the state the previous wave left, not with the
/// run's baseline: an error a first wave introduced is that wave's, and is not blamed again on every
/// wave after it. The run's own closing comparison with its baseline is unchanged.</para>
/// </summary>
internal sealed class WaveLedger
{
    /// <summary>At most this many trials for one regression; past it the attribution says so.</summary>
    internal const int MaxTrials = 8;

    private readonly List<WaveStep> _steps = new();
    private readonly object _gate = new();

    public WaveLedger(IReadOnlyList<BuildBaseline> reference) => Reference = reference;

    /// <summary>What each target reported where the last validated wave left it - at first, the run's baseline.</summary>
    public IReadOnlyList<BuildBaseline> Reference { get; set; }

    /// <summary>The files the build reads, as the current wave found them.</summary>
    public WaveCapture? Before { get; set; }

    /// <summary>The last validation's results and the files they were taken from, for the run's closing check.</summary>
    public (WaveCapture Files, IReadOnlyDictionary<string, CriterionResult> Results)? LastValidated { get; set; }

    /// <summary>How many waves have been closed; the next one's number is this plus one.</summary>
    public int Closed { get; private set; }

    public void Finished(WaveStep step)
    {
        lock (_gate) _steps.Add(step);
    }

    /// <summary>The steps that ended in the current wave, in the order they ended.</summary>
    public IReadOnlyList<WaveStep> Steps
    {
        get { lock (_gate) return _steps.ToArray(); }
    }

    /// <summary>Whether this wave changed what <paramref name="ecosystem"/>'s build reads (6.1).</summary>
    public bool Requires(IEcosystem ecosystem) => Steps.Any(s => s.Changes(ecosystem));

    public void Close()
    {
        lock (_gate) _steps.Clear();
        Closed++;
    }

    /// <summary>"steps 2 and 3", for sentences about this wave.</summary>
    public static string Span(IReadOnlyList<WaveStep> steps)
    {
        var nos = steps.Select(s => s.No).Order().ToArray();
        return nos.Length == 1 ? $"step {nos[0]}"
            : $"steps {string.Join(", ", nos[..^1])} and {nos[^1]}";
    }

    /// <summary>
    /// The attribution itself. <paramref name="trial"/> puts the workspace in the state "before the wave,
    /// plus these files as the wave left them", runs the builds, and says whether the regression came
    /// back (null: it could not tell). Restoring the workspace afterwards is the caller's.
    /// </summary>
    public static async Task<Attribution> AttributeAsync(IReadOnlyList<WaveStep> steps, IReadOnlySet<string> changed,
        Func<IReadOnlySet<string>, Task<bool?>> trial)
    {
        // Which files of the change each step recorded writing; what nobody recorded is a group of its own.
        var groups = new List<(WaveStep? Step, HashSet<string> Files)>();
        foreach (var step in steps)
        {
            var mine = changed.Where(c => step.Wrote.Contains(c, StringComparer.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (mine.Count > 0) groups.Add((step, mine));
        }
        var unclaimed = changed.Where(c => !groups.Any(g => g.Files.Contains(c))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commanders = steps.Where(s => s.UnrecordedWrites).ToArray();
        if (unclaimed.Count > 0) groups.Add((null, unclaimed));

        string Names(IEnumerable<WaveStep> s) => string.Join(", ", s.Select(x => x.Name));
        string UnclaimedWords() => $"{unclaimed.Count} changed file(s) no step recorded writing ("
            + string.Join(", ", unclaimed.Take(3)) + (unclaimed.Count > 3 ? ", ..." : "") + ")"
            + (commanders.Length > 0 ? $" - a command of {Names(commanders)}, or something outside the run" : " - something outside the run");

        if (groups.Count == 0)
            return new(null, [], "nothing the build reads changed in this wave, so the cause is outside it - the machine, "
                + "or a file no ecosystem claims", 0);

        // One candidate: nothing else the wave did can change what the build reads, and no trial is needed.
        if (groups.Count == 1)
            return groups[0].Step is { } only
                ? new(only, [only], $"{only.Name} made the only changes to what the build reads in this wave", 0)
                : new(null, commanders, $"the only changes to what the build reads are {UnclaimedWords()}", 0);

        if (groups.Count > MaxTrials)
            return new(null, groups.Select(g => g.Step).OfType<WaveStep>().ToArray(),
                $"{groups.Count} steps changed what the build reads; trying more than {MaxTrials} alone was not attempted", 0);

        var reproducing = new List<(WaveStep? Step, HashSet<string> Files)>();
        var unknown = 0;
        foreach (var group in groups)
        {
            var came = await trial(group.Files);
            if (came is null) unknown++;
            else if (came.Value) reproducing.Add(group);
        }
        var trials = groups.Count;
        var suspects = groups.Select(g => g.Step).OfType<WaveStep>().ToArray();

        if (unknown > 0 && reproducing.Count == 0)
            return new(null, suspects, $"{unknown} of {trials} trial build(s) could not run, so no step could be tried alone", trials);

        if (reproducing.Count == 1 && unknown == 0)
        {
            var (step, files) = reproducing[0];
            if (step is null)
                return new(null, commanders, $"it comes back from {UnclaimedWords()} alone", trials);
            var shared = groups.Where(g => g.Step is not null && g.Step != step && g.Files.Overlaps(files))
                .Select(g => g.Step!).ToArray();
            if (shared.Length > 0)
                return new(null, [step, .. shared], $"{step.Name}'s changes alone reproduce it, but {Names(shared)} "
                    + "wrote the same file(s), so what is on disk is not one step's alone", trials);
            return new(step, [step], $"{step.Name}'s changes alone reproduce it; no other step's do", trials);
        }

        if (reproducing.Count == 0)
            return new(null, suspects, "no single step's changes reproduce it alone - it takes them together: "
                + Names(suspects), trials);

        var several = reproducing.Select(r => r.Step).OfType<WaveStep>().ToArray();
        return new(null, several, $"{reproducing.Count} of the wave's changes each reproduce it alone"
            + (several.Length > 0 ? ": " + Names(several) : "")
            + (reproducing.Any(r => r.Step is null) ? (several.Length > 0 ? ", and " : ": ") + UnclaimedWords() : "")
            + (unknown > 0 ? $"; {unknown} trial(s) could not run" : ""), trials);
    }
}
