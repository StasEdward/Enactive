namespace Enactive.Agents;

/// <summary>A kind of thing a step can be told it still owes before it ends - one per rule of StepEnding that tells.</summary>
internal enum Obligation
{
    /// <summary>A call the reply described in text instead of making.</summary>
    DescribedCall,

    /// <summary>A criterion the plan attached to the step fails.</summary>
    Criteria,

    /// <summary>The step's declared output has not been handed on.</summary>
    Output,

    /// <summary>Calls that did not go through and that nothing has made good.</summary>
    OpenCalls
}

/// <summary>
/// How many times one step may be told of what it owes (Plan Phase 2): per kind of obligation, and how many reminders
/// the loop may add in all. The default is what the engine did when each kind had a once-only flag of its own - once per
/// kind, nothing added - and not "once in all", which would have let a criteria reminder use up the reminder to hand the
/// result on.
/// </summary>
/// <param name="PerKind">How many times a step may be told of one kind of obligation. 0: never told.</param>
/// <param name="Ceiling">How many reminders beyond the first of each kind a step may be told, all kinds together. The
/// first of each kind is never counted and never held back by it: when it counted every reminder, a step told twice of
/// its criteria and twice of its result at two per kind had spent a ceiling of four, and was never told of its open
/// calls - the reminder that saved run 0cf51c. Null: every added reminder the limit per kind allows,
/// (PerKind - 1) x the number of kinds, so raising PerKind does not leave a ceiling sized for one per kind behind it.</param>
public sealed record ObligationNudgeLimits(int PerKind = 1, int? Ceiling = null);

/// <summary>
/// The limits for a run somebody watches and for one nobody does. Which one a run is, is the run's own property - where it
/// was started (RunComposer.Attended) - and not whether its decisions can approve anything: "--approve deny" at a
/// console cannot approve and is watched all the same.
/// </summary>
public sealed record ObligationNudges
{
    public static ObligationNudges Default { get; } = new();

    /// <summary>A run somebody is at a screen for: the window's or the console's command bar, the browser.</summary>
    public ObligationNudgeLimits Attended { get; init; } = new();

    /// <summary>A run nobody watches: in the background, on a schedule, from a phone.</summary>
    public ObligationNudgeLimits Unattended { get; init; } = new();

    public ObligationNudgeLimits For(bool unattended) => unattended ? Unattended : Attended;
}

/// <summary>
/// Where a step stands on what it owes, as the engine sees it - built only from the engine's own state, never from what
/// the model wrote: which of the step's own criteria fail, whether its result is still owed and what its last refused
/// submission lacked, which calls are still open, and a call it described instead of making. A model that says "I fixed
/// it" and changed nothing stands where it stood.
/// </summary>
/// <param name="OutputOwed">The step declared an output and nothing has been accepted. Part of the position on its own:
/// a step that has sent nothing yet owes its result as much as one whose submission was refused.</param>
/// <param name="MissingFields">Required fields its last refused submission left out (StepOutputSlot).</param>
/// <param name="InvalidFields">Fields its last refused submission sent wrong (StepOutputSlot).</param>
/// <param name="OpenCalls">The identities of the calls still open (OpenFailures.Keys) - not their error text.</param>
/// <param name="FailingCriteria">The step's failing criteria, each by its place among the step's criteria and its name -
/// not the name alone, which two criteria may share.</param>
/// <param name="Described">The call the turn described in text, by name and arguments, when it did. Arguments included:
/// a step that describes a different call - another path, another command - has moved, and may be asked to send that one
/// properly too. Acceptable because the per-kind limit still caps it, and the same call described again, which is the
/// shape that burned iterations before the cap existed, is a position seen before and is let go.</param>
internal sealed record ObligationSignature(
    int? Step,
    IReadOnlyList<string> FailingCriteria,
    bool OutputOwed,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> InvalidFields,
    IReadOnlyList<string> OpenCalls,
    string? Described)
{
    /// <summary>Nothing is owed: no criterion fails, no result is owed, no call is open or described.</summary>
    public bool Empty => FailingCriteria.Count == 0 && !OutputOwed && OpenCalls.Count == 0 && Described is null;

    /// <summary>The position as one string, every list in one order, so the same state is the same key whatever order it was read in.</summary>
    public string Key
        => string.Join("\u001f",
            $"step:{Step}",
            "criteria:" + Sorted(FailingCriteria),
            "output:" + (OutputOwed ? "owed" : "handed on"),
            "missing:" + Sorted(MissingFields),
            "invalid:" + Sorted(InvalidFields),
            "open:" + Sorted(OpenCalls),
            "described:" + Described);

    private static string Sorted(IReadOnlyList<string> items) => string.Join("\u001e", items.Order(StringComparer.Ordinal));
}

/// <summary>What the loop decided about one obligation, and why - said on the timeline.</summary>
/// <param name="Told">The nudges of this kind so far, this one included.</param>
/// <param name="Limit">The most of this kind the step may be told.</param>
/// <param name="Announce">A release not said before for this kind and this reason: the timeline says it once, not every turn.</param>
internal readonly record struct ObligationDecision(bool Nudge, string Why, int Told, int Limit, bool Announce);

/// <summary>
/// Whether a step is told, again, of something it owes before it ends (Plan Phase 2) - pure: it reads nothing and says
/// nothing, StepEnding asks it and does the telling.
///
/// <para><b>Why.</b> Each kind of obligation was told once, by a flag of its own. A nudge that worked in part - four of six
/// fields handed on, one of two failing calls made good - was followed by the end of the step anyway, as if nothing had
/// come of it. Like AI-DLC's Stop hook, the loop keeps telling while the step's POSITION changes and lets go when it does
/// not: a position not seen before for this kind is progress and earns another nudge; one seen before is not, so A -> B
/// -> A cannot reset the count, and the per-kind limit and the ceiling hold however the position moves.</para>
///
/// <para><b>Its lifetime</b> is one attempt at the step: StepEnding makes one per tool loop - per attempt, per repair -
/// and a handover inside the attempt keeps it. A step tried again after a review is told afresh; within an attempt, a
/// handover that empties the conversation does not buy it the reminders it has already had.</para>
/// </summary>
internal sealed class ObligationLoop(ObligationNudgeLimits limits)
{
    /// <summary>The kinds of obligation, and so the compatible default ceiling.</summary>
    public const int KindCount = 4;

    /// <summary>
    /// The most a step is told of one kind, whatever the settings say. Every nudge is a whole model turn, paid with the
    /// whole prompt - on a local model with an 8k window that is the room the work itself needed - and a step that has
    /// moved three times on one obligation and still owes it is going round, not converging. The bench measures 1, 2 and
    /// 3 (Plan Phase 2, Measurement); nothing above was measured, so nothing above is allowed. AI-DLC caps its Stop hook
    /// at 2 attended and 8 unattended, for a stage of work rather than a step, which is why its numbers are not copied.
    /// </summary>
    public const int HardPerKind = 3;

    /// <summary>The limit per kind, clamped to 0..<see cref="HardPerKind"/>.</summary>
    public int PerKind { get; } = Math.Clamp(limits.PerKind, 0, HardPerKind);

    /// <summary>
    /// The most reminders the loop adds over all kinds - beyond the first of each, which it never counts - clamped to
    /// 0..what every kind at its hard limit adds up to. Unset, it is every added reminder <see cref="PerKind"/> allows:
    /// at one per kind nothing, as before the loop.
    /// </summary>
    public int Ceiling { get; } = Math.Clamp(limits.Ceiling ?? (Math.Clamp(limits.PerKind, 0, HardPerKind) - 1) * KindCount,
        0, (HardPerKind - 1) * KindCount);

    private readonly int[] _told = new int[KindCount];
    private readonly string?[] _released = new string?[KindCount];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _added;

    /// <summary>
    /// Whether this kind may be told again as far as its own limit goes: told at least once, and fewer times than
    /// <see cref="PerKind"/>. Only then is anything else - a carve-out, the ceiling, a position seen before - the reason
    /// a step is not told, and only then is that said on the timeline.
    /// </summary>
    public bool MayAdd(Obligation kind) => _told[(int)kind] is var told && told >= 1 && told < PerKind;

    /// <summary>Whether to tell the step of <paramref name="kind"/> now, standing where <paramref name="signature"/> says.</summary>
    public ObligationDecision Nudge(Obligation kind, ObligationSignature signature)
    {
        var k = (int)kind;
        // Let go in silence when nothing is owed, or when the kind has had as many reminders as its limit allows: at the
        // shipped limits that is every kind after its one reminder, and the engine before the loop said nothing when a
        // once-only flag was spent - "Not told again" on every step's ending, naming the limit, was noise.
        if (signature.Empty) return Quiet(kind, "nothing is owed");
        if (_told[k] >= PerKind)
            return Quiet(kind, PerKind switch
            {
                0 => "this run tells a step nothing of it",
                1 => "it was told once already",
                _ => $"it was told {PerKind} times already, the most for one kind"
            });
        if (_seen.Contains(k + "\u001d" + signature.Key))
            return Release(kind, "it was told of this already, and nothing the engine can see has changed since");
        // The ceiling caps only what the loop adds: the first reminder of each kind is the engine's as it always was, and
        // is never spent by reminders of other kinds (ObligationNudgeLimits.Ceiling).
        var first = _told[k] == 0;
        if (!first && _added >= Ceiling)
            return Release(kind, $"the step has been told again {_added} times in all, the most for one step");

        _seen.Add(k + "\u001d" + signature.Key);
        _told[k]++;
        if (!first) _added++;
        _released[k] = null;
        return new(true, "", _told[k], PerKind, false);
    }

    /// <summary>A release that is not said: nothing more would have been told anyway.</summary>
    private ObligationDecision Quiet(Obligation kind, string why) => new(false, why, _told[(int)kind], PerKind, false);

    /// <summary>
    /// Lets <paramref name="kind"/> go without telling: a reminder the loop would have added was refused, by the loop (a
    /// position seen before, the ceiling) or by a rule of the step's (it is blocked, the window is full). Announced the
    /// first time for this reason only, so a release that holds over several turns is said once.
    /// </summary>
    public ObligationDecision Release(Obligation kind, string why)
    {
        var k = (int)kind;
        var announce = _released[k] != why;
        _released[k] = why;
        return new(false, why, _told[k], PerKind, announce);
    }

    /// <summary>How many times this attempt has told the step of <paramref name="kind"/>.</summary>
    public int TimesTold(Obligation kind) => _told[(int)kind];

    /// <summary>What the timeline says of a nudge: once, as the engine always said it, or which of how many.</summary>
    public static string Told(ObligationDecision decision)
        => decision.Limit == 1 ? "The step is told, once." : $"The step is told ({decision.Told} of at most {decision.Limit}).";

    /// <summary>What the timeline says of a release: which obligation, and why.</summary>
    public static string NotToldAgain(Obligation kind, ObligationDecision decision)
        => $"Not told again of {Name(kind)}: {decision.Why}.";

    private static string Name(Obligation kind) => kind switch
    {
        Obligation.DescribedCall => "a call described in text instead of made",
        Obligation.Criteria => "the plan's criteria for this step",
        Obligation.Output => $"the result it is to hand on with {StepOutputContract.ToolName}",
        _ => "the calls still open"
    };
}
