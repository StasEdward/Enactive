namespace Enactive.Agents;

using System.Globalization;
using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>
/// How many characters of tool evidence one review is shown: the number a person set (EngineOptions.EvidenceBudget), or,
/// when nobody set one, a number that follows the REVIEWER's context window.
///
/// <para><b>Why automatic.</b> The budget was one fixed number, 6,000 characters, tuned for a weak local reviewer. The
/// same number was used for a reviewer with a 1,000,000-token window: in run 2eedd5 (2026-10-20, claude-haiku-5-5) a
/// read-heavy step's review was shown about 1,000 of the 14,500 characters of one read, and failed a correct step for
/// files it said were never read. The two reviews of that step needed about 38,600 and 65,000 characters to show every
/// call's output whole, in prompts of about 5,200 tokens. A number that suits one reviewer is not a property of the
/// engine; the window is a property of the reviewer, and the engine already knows it
/// (<see cref="IChatProvider.ContextWindow"/>) and already fits generations to it (<see cref="GenerationAllowance"/>).</para>
///
/// <para>Worked out for each review call, from the provider and model that review goes to: a plan whose step reviews and
/// whole-run review route to different models gives each the budget of its own window.</para>
/// </summary>
public static class ReviewEvidence
{
    /// <summary>
    /// The automatic budget never goes above this many characters, however large the window.
    ///
    /// <para>72,000 characters is about 24,000 tokens at the engine's rate (<see cref="TokenScale.DefaultCharsPerToken"/>).
    /// It holds both reviews of run 2eedd5 whole - about 38,600 and 65,000 characters of calls - and it is about what the
    /// worker itself had in front of it at the end of that step (a 27,285-token prompt, most of it these same tool results).
    /// Past it the reviewer is shown more than the worker could have used at once: evidence that dilutes the reviewer's
    /// attention over text no claim rests on, paid for on every review of every step and every retry. A step whose calls
    /// come to more than this is shown what the journal shows of any long step - each call, with a share of its output,
    /// and what is not shown said (ExecutionJournal.Describe).</para>
    /// </summary>
    public const int Ceiling = 72_000;

    /// <summary>
    /// The share of the window's free room the automatic budget takes: a quarter of what is left after the review's own
    /// prompt and the answer's reserve.
    ///
    /// <para>A quarter because both sizes are estimates. The prompt and the evidence are counted in characters at
    /// <see cref="TokenScale.DefaultCharsPerToken"/> (3) per token, and the engine allows a model's measured rate to be as
    /// low as 1.5 (TokenScale.Observe): tool output - code, JSON, text in another script - can cost up to twice the
    /// estimate. At that worst rate a quarter of the room still costs only half of it, and the rest is the margin for the
    /// prompt's own estimate being off the same way. Taken whole, an under-estimate would push the review past its window
    /// and cut off its answer, which costs the review rather than some evidence.</para>
    /// </summary>
    public const int ShareOfRoom = 4;

    /// <summary>The budget one review was given, and where it came from - said in the run's log once per review.</summary>
    /// <param name="Chars">Characters of evidence, the item account included.</param>
    /// <param name="Automatic">Worked out from the reviewer's window (or the default, when it has none); false when a person set it.</param>
    /// <param name="WindowTokens">The reviewer's window, when it states one.</param>
    /// <param name="WorkingTokens">The prompt size the reviewer's provider says its model works at, when that, and not the
    /// window, bound the budget (<see cref="WorkingSize"/>).</param>
    public readonly record struct Budget(int Chars, bool Automatic, int? WindowTokens, int? WorkingTokens = null)
    {
        /// <summary>One line for the run's log: the number, and whether it was automatic and from what.</summary>
        public string Describe()
        {
            var chars = Chars.ToString("N0", CultureInfo.InvariantCulture);
            if (!Automatic)
                return $"Evidence for the review: {chars} characters, as set in the settings.";
            if (WindowTokens is not { } window)
                return $"Evidence for the review: {chars} characters, automatic - the review model states no context window, "
                    + "so the fixed default.";
            var worked = WorkingTokens is { } working
                ? $", worked at {working.ToString("N0", CultureInfo.InvariantCulture)} tokens"
                : "";
            return $"Evidence for the review: {chars} characters, automatic - from the review model's "
                + $"{window.ToString("N0", CultureInfo.InvariantCulture)}-token window{worked} (a quarter of what its prompt and "
                + $"answer leave, between {ExecutionJournal.MinimumBudget.ToString("N0", CultureInfo.InvariantCulture)} "
                + $"and {Ceiling.ToString("N0", CultureInfo.InvariantCulture)}).";
        }
    }

    /// <summary>The budget for a review of <paramref name="promptChars"/> characters before its evidence, sent as <paramref name="review"/>.</summary>
    /// <param name="set">What a person set (EngineOptions.EvidenceBudget); null for automatic.</param>
    /// <param name="review">The request the review will send, without its messages: the window, the working size, the
    /// reasoning allowance and the reserve are the provider's answers for it, as they are when the review is sent.</param>
    public static Budget For(int? set, IChatProvider provider, ChatRequest review, int promptChars)
    {
        var scale = new TokenScale();
        // The answer's reserve: what the review asks to generate, its reasoning included - what GenerationAllowance.Fit
        // will keep for it - or the reserve somebody configured for this provider, when that is larger.
        var answer = Math.Max(
            GenerationAllowance.Total(review.OutputTokenLimit ?? 2048, provider.ReasoningAllowance(review)),
            provider.AnswerReserve(review) is int configured and > 0 ? configured : 0);
        return For(set, provider.ContextWindow(review), scale.TokensFor(promptChars), answer,
            provider.WorkingContext(review), provider.HandoverAtPercent(review));
    }

    /// <summary>
    /// The rule itself: a person's number, or a quarter of the room the window leaves - held to the provider's working size
    /// where it states one - between the floor and the ceiling.
    /// </summary>
    /// <param name="workingTokens">ProviderConfig.WorkingContextTokens: the prompt size this model works steadily at.</param>
    /// <param name="handoverPercent">ProviderConfig.HandoverAtPercent: the share of the window a step is handed over at.</param>
    public static Budget For(int? set, int? windowTokens, int promptTokens, int answerTokens,
        int? workingTokens = null, int? handoverPercent = null)
    {
        // A person's number is used as it is, raised to the floor as it always was: below it the evidence cannot hold its
        // own header. Not held to the ceiling - the ceiling is the engine's guess at what is worth paying for, and a person
        // who set a number has made that call.
        if (set is { } chosen)
            return new(Math.Max(ExecutionJournal.MinimumBudget, chosen), Automatic: false, windowTokens);

        // A window nobody stated: nothing to derive from, so what the engine has always used.
        if (windowTokens is not (> 0 and var window))
            return new(ExecutionJournal.DefaultBudget, Automatic: true, null);

        // The prompt a review may grow to: the window less the answer - or less again, where the provider says its model
        // works at a smaller size. That setting is somebody saying "no further than this" for this model, and the worker
        // loop holds its steps to it (Orchestrator). For a slow or weak local reviewer it is the lever on prefill time and on
        // attention spread thin; held to the window alone, a local reviewer on a mixed team was given a cloud model's share.
        var bySize = (long)window - Math.Max(0, answerTokens);
        var working = WorkingSize(window, workingTokens, handoverPercent);
        var effective = working is { } w && w < bySize ? w : bySize;
        var room = Math.Max(0L, effective - Math.Max(0, promptTokens));
        var chars = (long)new TokenScale().CharsFor((int)Math.Min(int.MaxValue, room / ShareOfRoom));
        return new((int)Math.Clamp(chars, ExecutionJournal.MinimumBudget, Ceiling), Automatic: true, window,
            effective < bySize ? (int)effective : null);
    }

    /// <summary>
    /// The prompt size a provider says its model works at, in tokens: its working size, or its handover share of the window,
    /// the nearer when both are set - the two settings the worker loop reads for the same thing, each somebody saying "no
    /// further than this". Null when neither is set.
    /// </summary>
    private static long? WorkingSize(int window, int? workingTokens, int? handoverPercent)
    {
        long? working = workingTokens is > 0 and var wanted ? wanted : null;
        // Read as the worker loop reads it: a percentage strictly between 0 and 100, or nothing.
        long? byShare = handoverPercent is > 0 and < 100 and var pct ? (long)window * pct / 100 : null;
        return working is { } a && byShare is { } b ? Math.Min(a, b) : working ?? byShare;
    }

    /// <summary>
    /// The most the automatic budget can be for a reviewer with this window and working size - what the settings say
    /// "automatic" will be, before any step's prompt is known (a step whose own files fill the window gets less). The
    /// default when no window is stated.
    /// </summary>
    public static int UpTo(int? windowTokens, int? workingTokens = null, int? handoverPercent = null)
        => For(null, windowTokens, promptTokens: 0, answerTokens: 0, workingTokens, handoverPercent).Chars;
}
