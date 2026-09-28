namespace Enactive.Core.Templates;

/// <summary>How a changed criterion compares with the one it replaces (Phase 4.2).</summary>
public enum CriterionStrength
{
    /// <summary>Asks for more: whatever passes the new one passes the old.</summary>
    Stronger,

    /// <summary>Asks for exactly the same.</summary>
    Same,

    /// <summary>Asks for less.</summary>
    Weaker,

    /// <summary>
    /// Neither can be shown to ask for at least as much as the other. Treated as a relaxation
    /// (4.2): what the engine cannot prove is not stronger, and a definition of done must not
    /// loosen on the strength of something nobody could check.
    /// </summary>
    Incomparable
}

/// <summary>
/// One change to a run's definition of done, with who made it, why, what it was and what it became
/// (Phase 4.1) - and whether it was let through, and on whose say.
/// </summary>
/// <param name="DecidedBy">"engine" when it could be shown to be at least as strong; otherwise who answered the question.</param>
public sealed record ContractRevision(
    int Revision,
    DateTimeOffset At,
    string ChangedBy,
    string Reason,
    SuccessCriterionDefinition? Previous,
    SuccessCriterionDefinition? New,
    CriterionStrength Strength,
    string Why,
    bool Applied,
    string? DecidedBy);
