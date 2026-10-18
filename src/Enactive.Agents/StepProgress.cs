namespace Enactive.Agents;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Enactive.Core.Context;
using Enactive.Core.Tools;
using static Enactive.Agents.ToolCallParsing;

/// <summary>
/// Whether a step is still getting somewhere.
///
/// <para>"Somewhere" is deliberately cheap to define: a tool call, by name and arguments, that
/// this step has not made before. Success is not required — a command that fails teaches the
/// model something and an unresolved failure is already caught at the end of the loop — so what
/// is left is exactly repetition, which is what a stuck model actually does.</para>
///
/// <para>With one correction, which is the whole point of this class existing after a run that
/// proved it wrong. A REPAIR LOOP is made of repeated calls by construction: read the file,
/// edit it, build, read it again, edit again, build again. Counted naively, the second read and
/// the second build are "calls it had already made" — and a step that had just broken the build
/// and was in the middle of fixing it was stopped for doing the fixing. But those calls are not
/// the same calls: the file they read and the tree they build no longer exist as they were. So
/// a successful known novel mutation advances a GENERATION, and everything
/// that only observes — reads, commands — is identified together with the generation it
/// observed. After a real change, looking again is new.</para>
///
/// <para><b>A write is itself plus the workspace it is applied to.</b> Until 2026-10-19 a write was identified by the
/// call alone. Run da4b3b, step 3: asked to check that its tests fail when what they test is broken, the worker broke a
/// method, ran the tests, put the method back, ran them again, rewrote its tests - and broke the same line again. That
/// second break was applied to a workspace it had never been applied to (the tests had changed under it), yet it counted
/// as a repeat; it opened no generation, so the test run after it was a repeat of the run before it; and the restore
/// that would have undone the break was the third, so the step was stopped one call short of putting the code back. So
/// a write now carries a cheap STATE KEY (<see cref="_madeBy"/>): for each path the step's known writes touched, which
/// write last made it. The same edit after another file changed is applied to a new state and is new; break, put back,
/// break, put back with nothing else changing comes back to states already seen, and stalls as it always did.</para>
///
/// <para>Two bounds keep the escape from a stall costly. A path is keyed by the write ALONE, not by what that write was
/// applied to: an edit's result does depend on what it edits, but keyed that way an alternation would never come back
/// to a state it had seen, and two edits undoing each other would be new forever. And one write counts as new in at most
/// <see cref="StatesPerWrite"/> states - many files each written a couple of ways make many states, and without a limit
/// each would buy the same write, and the looks after it, once more. Unknown shell effects still never manufacture
/// progress, and the tool loop's turn ceiling remains behind all of it for the case this cannot see.</para>
///
/// <para><b>Putting back its own change is let through.</b> Undoing a change the step itself made - restore_file of a
/// path it changed, or any tool the host marks <see cref="ToolDefinition.RestoresRunStart"/> - is progress by
/// definition, and stopping before it leaves the workspace as the step broke it (da4b3b: the step ended Incomplete, was
/// not sent back so nothing reverted it, and the run's final checks failed on the break). Such a turn is RUN and then
/// re-evaluated (<see cref="LetThroughToPutBack"/>), not counted as progress: counted, break / put back would reset the
/// count forever. It stays bounded because letting a turn through spends what licensed it - the paths it puts back - and
/// nothing written in that same turn licenses another; only a write in a turn that was not let through does, and that
/// turn is one the guard can stop.</para>
/// </summary>
internal sealed class StepProgress
{
    /// <summary>
    /// In how many different workspace states one write counts as new. Four is room for a temporary change made and put
    /// back around a few rewrites of what it is checked against (da4b3b made the same break twice); past that the same
    /// write is the same write wherever it lands. Without a limit, a model alternating a few writes across many files
    /// reaches many states, and each would buy every one of those writes, and every look after them, once more.
    /// </summary>
    internal const int StatesPerWrite = 4;

    private readonly IReadOnlyList<ToolDefinition> _definitions;
    public StepProgress(IReadOnlyList<ToolDefinition> definitions) => _definitions = definitions;

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<string> _repeats = new();

    /// <summary>
    /// Identities that ran to a SUCCESSFUL result at least once - as opposed to <see cref="_seen"/>,
    /// which only means "attempted". See <see cref="AlreadyRanExactly"/> for why the difference
    /// matters: a failed call and the same call declaring the failure expected are the SAME
    /// identity on purpose (<see cref="CallIdentity"/>), and that pairing is a recovery, not a
    /// repeat - it must stay allowed even though the first attempt is already "seen".
    /// </summary>
    private readonly HashSet<string> _succeeded = new(StringComparer.Ordinal);
    private HashSet<string> _succeededBeforeTurn = new(StringComparer.Ordinal);

    /// <summary>The states each write (by <see cref="CallIdentity"/>) has been made in, as the turn it was asked in began.</summary>
    private readonly Dictionary<string, HashSet<string>> _writesSeen = new(StringComparer.Ordinal);

    /// <summary>The states each write has CHANGED the workspace from - what opens a generation.</summary>
    private readonly Dictionary<string, HashSet<string>> _writesNovel = new(StringComparer.Ordinal);

    /// <summary>
    /// The state key's source: for each path a known write of this step changed, which write last made it (a hash of the
    /// call; a put-back is <see cref="RunStart"/>). Hashed, so a path costs a fixed size however large the content was.
    /// </summary>
    private readonly SortedDictionary<string, string> _madeBy = new(StringComparer.OrdinalIgnoreCase);
    private string _state = "";

    /// <summary>What a put-back leaves a path as: the bytes the run found, whichever write it undoes.</summary>
    private const string RunStart = "run-start";

    /// <summary>Paths this step changed and has not since put back (or tried to) - what licenses letting a put-back through.</summary>
    private readonly HashSet<string> _unrestored = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>This turn was let through to put back; its own writes license nothing.</summary>
    private bool _letThrough;

    /// <summary>Keep this batch's results out of repeat decisions until the next model turn.</summary>
    public void BeginTurn()
    {
        _succeededBeforeTurn = new(_succeeded, StringComparer.Ordinal);
        _letThrough = false;
    }

    /// <summary>Observation generation for stall detection, advanced only by novel known mutations.</summary>
    private int _generation;

    /// <summary>Turns in a row that did nothing new.</summary>
    public int Stalled { get; private set; }

    /// <summary>Effects invalidate repeat evidence independently of the novelty used by the stall guard.</summary>
    public void Resulted(ToolCall call, ToolResult result, long? before, long? after)
    {
        if (result.Success && result.WorkspaceEffect == WorkspaceEffect.None
            && before is { } version && after == before)
            _succeeded.Add(RepeatIdentity(call, version));

        var definition = DefinitionOf(call);

        // A put-back, whatever came of it, spends its licence: put back, already as it was, or not possible (a file the
        // run made, a change a command made) - asked again, it would say the same. Spent on the result and not only when
        // let through: an ordinary turn's put-back must not leave a licence for a later turn to use.
        if (definition?.RestoresRunStart == true)
            foreach (var path in ArgumentPaths(definition, call))
                _unrestored.Remove(path);

        // Unknown shell effects never manufacture progress. A known write opens a new observation generation once per
        // state it is applied to (StatesPerWrite at most), so alternating edits still stall.
        if (!result.Success || result.WorkspaceEffect != WorkspaceEffect.Changed)
            return;

        var identity = CallIdentity.Of(call);
        if (Admits(_writesNovel, identity, _state, record: true))
            _generation++;

        var paths = (result.ChangedPaths ?? (definition is null ? null : ToolEffects.Paths(definition, call.ArgumentsJson)))
            ?.Select(ShellLookup.Normal).ToArray();
        var made = definition?.RestoresRunStart == true ? RunStart : Hash(identity);
        // A write that names no path is its own slot: it can only add to the state, never take a path's place.
        foreach (var path in paths is { Length: > 0 } ? paths : ["\0" + Hash(identity)])
            _madeBy[path] = made;
        _state = Hash(string.Join("\n", _madeBy.Select(p => p.Key + "=" + p.Value)));

        if (definition?.RestoresRunStart != true && !_letThrough && paths is not null)
            foreach (var path in paths)
                _unrestored.Add(path);
    }

    /// <summary>
    /// Whether this exact call - same tool, same arguments, nothing this step has WRITTEN since -
    /// already ran to a SUCCESSFUL result in this step. Only a success counts: a call that FAILED
    /// and is now being retried with a declaration that makes the same outcome an answer
    /// (<c>expectedExitCodes</c>) shares its identity with the failure on purpose, and that retry
    /// is a recovery this must not block.
    ///
    /// <para>Check immediately before each call, using the current workspace generation but only
    /// successes captured by <see cref="BeginTurn"/>. A write earlier in this batch invalidates
    /// old observations; a success in this batch only gates calls in later model turns.</para>
    /// </summary>
    public bool AlreadyRanExactly(ToolCall call, long? version)
        => version is { } current && _succeededBeforeTurn.Contains(RepeatIdentity(call, current));

    private static string RepeatIdentity(ToolCall call, long version)
        => CallIdentity.Of(call) + "\0#" + version.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether every call is one this step has already made, with nothing it has changed since - without recording
    /// anything. Asked before <see cref="Advanced"/>, which records. A put-back of the step's own change is never
    /// "nothing changed since": it changes the file back (a step that said it was done and repeated one would otherwise
    /// end on its message with the put-back not run).
    /// </summary>
    public bool OnlyRepeats(IReadOnlyList<ToolCall> calls)
        => calls.Count > 0 && calls.All(call => !IsNew(call, record: false)) && !PutsBackOwnChange(calls);

    /// <summary>Records one turn's calls and says whether any of them was new.</summary>
    public bool Advanced(IReadOnlyList<ToolCall> calls)
    {
        var advanced = false;
        var repeatedHere = new List<string>();

        foreach (var call in calls)
            if (IsNew(call, record: true))
                advanced = true;
            else
                repeatedHere.Add($"{call.Name} {Compact(call.ArgumentsJson)}");

        if (advanced)
        {
            Stalled = 0;
            _repeats.Clear();
        }
        else
        {
            Stalled++;
            foreach (var repeat in repeatedHere)
                if (!_repeats.Contains(repeat))
                    _repeats.Add(repeat);
        }

        return advanced;
    }

    /// <summary>Whether any of these calls would put back a change this step made and has not put back.</summary>
    public bool PutsBackOwnChange(IReadOnlyList<ToolCall> calls) => calls.Any(call => PutBackPaths(call).Any());

    /// <summary>
    /// Lets a turn the guard would stop run when it puts back the step's own change, spending the licence: the paths it
    /// puts back license no further turn, and nothing it writes licenses one (see the class remarks for why this bounds
    /// it). False when the turn puts nothing of the step's own back - stop it as before.
    /// </summary>
    public bool LetThroughToPutBack(IReadOnlyList<ToolCall> calls)
    {
        var paths = calls.SelectMany(PutBackPaths).ToArray();
        if (paths.Length == 0) return false;
        foreach (var path in paths)
            _unrestored.Remove(path);
        _letThrough = true;
        return true;
    }

    private IEnumerable<string> PutBackPaths(ToolCall call)
        => DefinitionOf(call) is { RestoresRunStart: true } definition
            ? ArgumentPaths(definition, call).Where(_unrestored.Contains)
            : [];

    private static IEnumerable<string> ArgumentPaths(ToolDefinition definition, ToolCall call)
        => ToolEffects.Paths(definition, call.ArgumentsJson)?.Select(ShellLookup.Normal) ?? [];

    private ToolDefinition? DefinitionOf(ToolCall call) => _definitions.FirstOrDefault(d => d.Name == call.Name);

    /// <summary>
    /// Whether this call is one this step has not made HERE. A write (an <see cref="ProgressIdentity.Action"/>) is itself
    /// in the state its known writes have left; anything else is itself plus the generation it is about to look at. Both
    /// as the turn began: a write earlier in the same turn is not yet a result.
    /// </summary>
    private bool IsNew(ToolCall call, bool record)
        => _definitions.Any(d => d.Name == call.Name && d.ProgressIdentity == ProgressIdentity.Action)
            ? Admits(_writesSeen, CallIdentity.Of(call), _state, record)
            : record
                ? _seen.Add(ObservationIdentity(call))
                : !_seen.Contains(ObservationIdentity(call));

    private string ObservationIdentity(ToolCall call)
        => CallIdentity.Of(call) + "\0#" + _generation.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="identity"/> is new in <paramref name="state"/>: not made there, and still under <see cref="StatesPerWrite"/>.</summary>
    private static bool Admits(Dictionary<string, HashSet<string>> made, string identity, string state, bool record)
    {
        made.TryGetValue(identity, out var states);
        if (states is not null && (states.Contains(state) || states.Count >= StatesPerWrite))
            return false;
        if (record)
            (states ?? (made[identity] = new HashSet<string>(StringComparer.Ordinal))).Add(state);
        return true;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16);

    /// <summary>What it kept asking for, for the message that stops it.</summary>
    public string Describe()
        => _repeats.Count == 0 ? "no new tool calls" : string.Join("; ", _repeats);
}
