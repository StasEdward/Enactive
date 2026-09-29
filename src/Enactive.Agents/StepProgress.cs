namespace Enactive.Agents;

using System.Globalization;
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
/// <para>The writes themselves carry no generation, which is what keeps this bounded: escaping
/// a stall costs a write nobody has made before. Two edits alternating forever are still two
/// calls already made, and still stall. And the tool loop's turn ceiling remains behind all of
/// it for the case this cannot see.</para>
/// </summary>
internal sealed class StepProgress
{
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
    private readonly HashSet<string> _novelMutations = new(StringComparer.Ordinal);
    private HashSet<string> _succeededBeforeTurn = new(StringComparer.Ordinal);

    /// <summary>Keep this batch's results out of repeat decisions until the next model turn.</summary>
    public void BeginTurn() => _succeededBeforeTurn = new(_succeeded, StringComparer.Ordinal);

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

        // Unknown shell effects never manufacture progress. Even a known write only opens a
        // new observation generation once per distinct action, so alternating edits still stall.
        if (result.Success && result.WorkspaceEffect == WorkspaceEffect.Changed
            && _novelMutations.Add(CallIdentity.Of(call)))
            _generation++;
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
    /// anything. Asked before <see cref="Advanced"/>, which records.
    /// </summary>
    public bool OnlyRepeats(IReadOnlyList<ToolCall> calls)
        => calls.Count > 0 && calls.All(call => _seen.Contains(Identity(call)));

    /// <summary>Records one turn's calls and says whether any of them was new.</summary>
    public bool Advanced(IReadOnlyList<ToolCall> calls)
    {
        var advanced = false;
        var repeatedHere = new List<string>();

        foreach (var call in calls)
            if (_seen.Add(Identity(call)))
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

    /// <summary>
    /// What makes this call this call, HERE. A write is itself; anything else is itself plus the
    /// state of the workspace it is about to look at.
    /// </summary>
    private string Identity(ToolCall call)
        => _definitions.Any(d => d.Name == call.Name && d.ProgressIdentity == ProgressIdentity.Action)
            ? CallIdentity.Of(call)
            : CallIdentity.Of(call) + "\0#" + _generation.ToString(CultureInfo.InvariantCulture);

    /// <summary>What it kept asking for, for the message that stops it.</summary>
    public string Describe()
        => _repeats.Count == 0 ? "no new tool calls" : string.Join("; ", _repeats);
}
