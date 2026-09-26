namespace Enactive.Core.Execution;

using Enactive.Core.Templates;

/// <summary>
/// What a run is allowed to spend, and how much of it is gone.
///
/// <para><see cref="ExecutionLimits"/> shipped in M0 as data and was read by NOTHING: a template
/// could declare <c>MaxSteps: 12</c> and the run would happily take fifty. A limit that is stored,
/// shown in an editor and never enforced is worse than no limit, because it is written down
/// somewhere as a guarantee.</para>
///
/// <para>Checked before dispatching a step and before requesting a new model turn.
/// An in-flight reply and its complete tool batch finish before the next check; one turn may
/// overshoot. Step limits apply only to dispatch, never to an already-started step.</para>
/// </summary>
public sealed class RunBudget
{
    private readonly ExecutionLimits _limits;
    private readonly DateTimeOffset _startedAt;
    private readonly Func<DateTimeOffset> _now;

    private int _steps;
    private long _tokens;

    /// <param name="stepsAlreadyRun">
    /// Steps a previous, interrupted attempt at this work already dispatched. Carried across a
    /// resume so a run cannot escape its step limit by being interrupted and picked up again.
    /// </param>
    /// <param name="tokensAlreadySpent">
    /// Tokens that attempt already spent. Carried for the same reason - and because they were
    /// genuinely paid for.
    /// </param>
    public RunBudget(
        ExecutionLimits? limits,
        DateTimeOffset startedAt,
        Func<DateTimeOffset>? now = null,
        int stepsAlreadyRun = 0,
        long tokensAlreadySpent = 0)
    {
        _limits = limits ?? ExecutionLimits.None;
        // For a RESUMED run this is when the resume started, not when the original attempt did.
        // Steps and tokens carry across; elapsed TIME does not, because nothing was running while
        // the app was closed. Counting a night's sleep against a duration limit would mean a run
        // interrupted in the evening can never be resumed, which is not a limit anybody set.
        _startedAt = startedAt;
        // Injectable so the duration limit can be tested without waiting for it.
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _steps = stepsAlreadyRun;
        _tokens = tokensAlreadySpent;
    }

    /// <summary>A run with nothing to spend against - the behaviour before limits existed.</summary>
    public static RunBudget Unlimited() => new(ExecutionLimits.None, DateTimeOffset.UtcNow);

    public int StepsRun => Volatile.Read(ref _steps);
    public int RemainingSteps => _limits.MaxSteps is { } max ? Math.Max(0, max - StepsRun) : int.MaxValue;
    public long TokensSpent => Interlocked.Read(ref _tokens);

    /// <summary>Counts a step as started. Called when one is DISPATCHED, not when it finishes.</summary>
    public void StepStarted() => Interlocked.Increment(ref _steps);

    /// <summary>
    /// Adds a turn's tokens. Every phase counts - planning, execution and review - because the
    /// budget is what the RUN costs, and a reviewer on a large cloud model can be the larger half
    /// of it.
    /// </summary>
    public void TokensUsed(int prompt, int completion)
    {
        var total = (long)prompt + completion;
        if (total > 0)
            Interlocked.Add(ref _tokens, total);
    }

    /// <summary>
    /// The limit this run has reached, in words a person can act on - or null while it is still
    /// within all of them.
    ///
    /// <para>Words rather than an enum because this ends up in the terminal event's reason, next to
    /// "2 step(s) failed", and the number that was hit is the useful part of it.</para>
    /// </summary>
    public string? Exhausted
    {
        get
        {
            if (_limits.MaxSteps is { } maxSteps && StepsRun >= maxSteps)
                return $"the run reached its limit of {maxSteps} step(s)";

            return TurnExhausted;
        }
    }

    /// <summary>Token/time ceiling at a completed-turn boundary; excludes already-dispatched steps.</summary>
    public string? TurnExhausted
    {
        get
        {
            if (_limits.MaxTokens is { } maxTokens && TokensSpent >= maxTokens)
                return $"the run reached its limit of {maxTokens} token(s), having used {TokensSpent}";

            if (_limits.MaxDurationSeconds is { } maxSeconds)
            {
                var elapsed = _now() - _startedAt;
                if (elapsed.TotalSeconds >= maxSeconds)
                    return $"the run reached its limit of {maxSeconds} second(s), having taken {(int)elapsed.TotalSeconds}";
            }

            return null;
        }
    }

    /// <summary>Check a pending phase's reported usage before it is published/accounted once by its caller.</summary>
    public string? TurnExhaustedAfter(int prompt, int completion)
        => _limits.MaxTokens is { } max && TokensSpent + (long)prompt + completion >= max
            ? $"the run reached its limit of {max} token(s), having used {TokensSpent + (long)prompt + completion}"
            : TurnExhausted;
}
