namespace Enactive.Core.Tools;

using Enactive.Core.Permissions;

/// <summary>A tool the model was NOT shown, and the reason it was not.</summary>
/// <param name="Reason">
/// Written to be readable in a run report, because that is where it has to land. A tool withheld
/// silently is a worse failure than the one this fixes: the report would name a plan that could
/// never have worked, with nothing in it saying why.
/// </param>
public sealed record WithheldTool(string Name, string Reason);

/// <summary>
/// Which tools a run may actually use, and which were kept back.
///
/// <para><b>Why this exists.</b> A scheduled run on the Execute tier was handed all thirteen
/// registered tools while its decision handler was <see cref="PermissionDecision.Deny"/> by
/// construction - nobody is awake at 00:14 to approve anything. Four of the thirteen could not
/// succeed once, and nothing said so, so the model found out the only way it could: by calling
/// them. Six refusals, seven calls to the worker model, 28 167 prompt tokens, to discover a
/// decision that had been taken before the first of them.</para>
///
/// <para>The rule is one sentence: <b>a tool whose gate can only end in refusal has no business
/// being advertised.</b> It is not "cache the refusals" - at an interactive console a person who
/// says no once may say yes next time, and that question is real. It holds only where the answer
/// is fixed before it is asked.</para>
/// </summary>
public sealed record ToolOffer(IReadOnlyList<string> Offered, IReadOnlyList<WithheldTool> Withheld)
{
    /// <summary>Whether this tool was kept back, however it comes to be called later.</summary>
    public bool Withholds(string tool) => Reason(tool) is not null;

    /// <summary>
    /// Why this tool was kept back, or null if it was not.
    ///
    /// <para>The second half of the rule lives here. A withheld tool can still be CALLED - a name
    /// remembered from earlier in the transcript, or invented - and when it is, this reason is the
    /// answer. Asking a handler that cannot say yes would cost a round trip to arrive at the same
    /// refusal, which is the very waste the withholding removes.</para>
    /// </summary>
    public string? Reason(string tool)
    {
        foreach (var held in Withheld)
        {
            if (string.Equals(held.Name, tool, StringComparison.OrdinalIgnoreCase))
                return held.Reason;
        }
        return null;
    }

    /// <summary>
    /// Why the kept-back tools were kept back, or null when none were: "git — blocked by this run's
    /// permission policy; run_command, docker — needs an approval nobody is there to give".
    ///
    /// <para>Grouped by reason rather than listed one per tool: four tools withheld for the same
    /// reason are one fact, and reading it four times invites the reader to look for four
    /// causes.</para>
    ///
    /// <para>Separate from <see cref="Sentence"/> because two different readers need it. The run
    /// record wants a whole sentence; the schedules window is explaining why a template cannot do
    /// its job and needs this as a clause inside a sentence of its own. Grouping it twice is how
    /// the two would drift apart.</para>
    /// </summary>
    public string? Because
    {
        get
        {
            if (Withheld.Count == 0)
                return null;

            var groups = new List<(string Reason, List<string> Tools)>();

            foreach (var held in Withheld)
            {
                var group = groups.FirstOrDefault(g => g.Reason == held.Reason);
                if (group.Tools is null)
                {
                    group = (held.Reason, new List<string>());
                    groups.Add(group);
                }
                group.Tools.Add(held.Name);
            }

            return string.Join("; ", groups.Select(g => $"{string.Join(", ", g.Tools)} — {g.Reason}"));
        }
    }

    /// <summary>What to put in the run record, or null when nothing was kept back.</summary>
    public string? Sentence => Because is { } because ? $"Withheld from the model: {because}." : null;
}

/// <summary>Works out a <see cref="ToolOffer"/> from a policy and the run's decision handler.</summary>
public static class ToolOffers
{
    /// <summary>A tool the policy forbids outright. True of every run, watched or not.</summary>
    public const string Blocked = "blocked by this run's permission policy";

    /// <summary>
    /// A tool the policy would ask about, in a run where asking cannot produce a yes. True only of
    /// an unattended run or a fixed refusal - never of a person at a console or a card on screen.
    /// </summary>
    public const string Unanswerable = "needs an approval nobody is there to give";

    /// <param name="candidates">
    /// The tools this worker's role already carries. The role gate runs first and is a different
    /// question: what this ROLE may do, rather than what this RUN may do.
    /// </param>
    /// <param name="gate">
    /// The permission decision for a tool, as the engine would reach it at call time. Passed as a
    /// function so this rule is testable without a policy engine, a registry or a workspace - and
    /// so the caller folds in whatever else upgrades an Allow to an Ask.
    /// </param>
    /// <param name="approvalIsPossible">
    /// Whether the run's decision handler could ever answer "allow". False for an unattended run
    /// and for <c>--approve deny</c>; true for every handler with a person behind it, which is why
    /// this cannot simply be read off the policy.
    /// </param>
    public static ToolOffer For(
        IEnumerable<string> candidates,
        Func<string, PermissionDecision> gate,
        bool approvalIsPossible)
        => For(candidates, gate, _ => approvalIsPossible);

    /// <param name="approvalIsPossible">
    /// Per tool: whether the run's decision handler could ever answer "allow" for that tool - see
    /// <see cref="IDecisionHandler.CanApproveTool"/>.
    /// </param>
    public static ToolOffer For(
        IEnumerable<string> candidates,
        Func<string, PermissionDecision> gate,
        Func<string, bool> approvalIsPossible)
    {
        var offered = new List<string>();
        var withheld = new List<WithheldTool>();

        foreach (var tool in candidates)
        {
            switch (gate(tool))
            {
                case PermissionDecision.Deny:
                    withheld.Add(new WithheldTool(tool, Blocked));
                    break;

                // An Ask is a real question wherever somebody can answer it, and this is the line
                // the whole change balances on: withholding an ask-before tool from a watched run
                // would take away the approval prompt that IS the feature.
                case PermissionDecision.Ask when !approvalIsPossible(tool):
                    withheld.Add(new WithheldTool(tool, Unanswerable));
                    break;

                default:
                    offered.Add(tool);
                    break;
            }
        }

        return new ToolOffer(offered, withheld);
    }
}
