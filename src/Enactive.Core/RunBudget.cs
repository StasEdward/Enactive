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
/// <para><b>Checked BETWEEN steps, never inside one.</b> That is a real limitation and worth stating
/// rather than hiding: a single step that runs long or reads an enormous file overshoots, and the
/// budget notices only when it is asked for the next one. The alternative is cancelling work in
/// flight, which throws away whatever that step had already done and leaves the workspace in a state
/// nobody chose. A ceiling that stops the NEXT thing is honest; one that kills the current thing
/// costs more than it saves.</para>
/// </summary>
public sealed class RunBudget
{
    private readonly ExecutionLimits _limits;
    private readonly DateTimeOffset _startedAt;
    private readonly Func<DateTimeOffset> _now;

    private int _steps;
    private long _tokens;

    public RunBudget(
        ExecutionLimits? limits,
        DateTimeOffset startedAt,
        Func<DateTimeOffset>? now = null)
    {
        _limits = limits ?? ExecutionLimits.None;
        _startedAt = startedAt;
        // Injectable so the duration limit can be tested without waiting for it.
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>A run with nothing to spend against - the behaviour before limits existed.</summary>
    public static RunBudget Unlimited() => new(ExecutionLimits.None, DateTimeOffset.UtcNow);

    public int StepsRun => Volatile.Read(ref _steps);
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
}
