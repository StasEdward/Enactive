namespace Enactive.Agents;

using Enactive.Core.Templates;

/// <summary>
/// Whether a changed criterion asks for at least as much as the one it replaces (Phase 4.2) - so
/// that the definition of done can tighten on its own, and cannot loosen without somebody saying so.
///
/// <para><b>Why this exists.</b> When a proposed check fails, the planner is asked whether the check
/// or the work is at fault, and may answer with a new command. That new command was run in place of
/// the old one, reviewed only against the task's restrictions. So a check that failed could be
/// replaced by one that passes - <c>dotnet test</c> by <c>dotnet test --filter OneTest</c>, or by
/// anything at all - and the run called finished, with nobody having agreed to the lower bar.</para>
///
/// <para><b>What can be proven.</b> Very little, and that is deliberate. Two different commands
/// cannot be compared: nothing short of running both against every workspace shows one checks at
/// least as much as the other, so they are <see cref="CriterionStrength.Incomparable"/>, and
/// incomparable is treated as a relaxation. The typed criteria are where an order exists: a file
/// that must exist and be non-empty asks more than one that must only exist; a file that must
/// contain a longer text that includes the old one asks more than the old. No longer required is
/// weaker; newly required is stronger.</para>
/// </summary>
public static class ContractMonotonicity
{
    public static bool AtLeastAsStrong(CriterionStrength strength) => strength is CriterionStrength.Same or CriterionStrength.Stronger;

    /// <summary>How <paramref name="now"/> compares with <paramref name="old"/>, and why - in words for the person asked.</summary>
    public static (CriterionStrength Strength, string Why) Compare(SuccessCriterionDefinition old, SuccessCriterionDefinition now)
    {
        var findings = new List<(CriterionStrength Strength, string Why)>();

        if (old.Required && !now.Required) findings.Add((CriterionStrength.Weaker, "it is no longer required"));
        else if (!old.Required && now.Required) findings.Add((CriterionStrength.Stronger, "it is now required"));

        findings.Add(What(old, now));

        var incomparable = findings.Where(f => f.Strength == CriterionStrength.Incomparable).ToArray();
        if (incomparable.Length > 0) return incomparable[0];
        var weaker = findings.Where(f => f.Strength == CriterionStrength.Weaker).ToArray();
        var stronger = findings.Where(f => f.Strength == CriterionStrength.Stronger).ToArray();
        if (weaker.Length > 0 && stronger.Length > 0)
            return (CriterionStrength.Incomparable, $"{weaker[0].Why}, and {stronger[0].Why}: neither contains the other");
        if (weaker.Length > 0) return weaker[0];
        if (stronger.Length > 0) return stronger[0];
        return (CriterionStrength.Same, "it asks for exactly the same");
    }

    private static (CriterionStrength, string) What(SuccessCriterionDefinition old, SuccessCriterionDefinition now)
    {
        if (old.Typed is null && now.Typed is null)
            return old.Command == now.Command && old.ExpectedExitCode == now.ExpectedExitCode
                ? (CriterionStrength.Same, "the same command")
                : (CriterionStrength.Incomparable,
                    "a different command cannot be shown to check at least as much as the one it replaces");

        if (old.Typed is not { } was || now.Typed is not { } @is || was.Kind != @is.Kind)
            return (CriterionStrength.Incomparable, "it is a different kind of check");

        switch (was.Kind)
        {
            case TypedCriterionKind.FileExists:
                if (!SamePath(was.Path, @is.Path)) return (CriterionStrength.Incomparable, "it is about a different file");
                if (was.NonEmpty == @is.NonEmpty) return (CriterionStrength.Same, "the same file, asked the same");
                return @is.NonEmpty
                    ? (CriterionStrength.Stronger, "the file must now also not be empty")
                    : (CriterionStrength.Weaker, "the file may now be empty");

            case TypedCriterionKind.FileContains:
                if (!SamePath(was.Path, @is.Path)) return (CriterionStrength.Incomparable, "it is about a different file");
                if (@is.Text == was.Text) return (CriterionStrength.Same, "the same file and text");
                return @is.Text is { } longer && was.Text is { } shorter && longer.Contains(shorter, StringComparison.Ordinal)
                    ? (CriterionStrength.Stronger, "the text it looks for now includes the old one")
                    : (CriterionStrength.Incomparable, "it looks for a text that does not include the old one");

            case TypedCriterionKind.EvidenceCoversAll:
                // Other items, other results or another kind of evidence: not shown to be a superset (4.2).
                return was.SourceStep == @is.SourceStep && was.SourceField == @is.SourceField
                       && was.ResultsStep == @is.ResultsStep && was.ResultsField == @is.ResultsField && was.Evidence == @is.Evidence
                    ? (CriterionStrength.Same, "the same items, results and evidence")
                    : (CriterionStrength.Incomparable, "coverage of other items or by other evidence cannot be shown to cover as much");

            default:
                // A test run narrowed or pointed elsewhere cannot be shown to cover what it covered.
                return string.Equals(was.Target, @is.Target, StringComparison.OrdinalIgnoreCase) && old.Command == now.Command
                    ? (CriterionStrength.Same, "the same tests")
                    : (CriterionStrength.Incomparable, "a different test run cannot be shown to cover what the old one did");
        }
    }

    private static bool SamePath(string? a, string? b)
        => string.Equals(a?.Replace('\\', '/').TrimStart('.', '/'), b?.Replace('\\', '/').TrimStart('.', '/'),
            StringComparison.OrdinalIgnoreCase);
}
