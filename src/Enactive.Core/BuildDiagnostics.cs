namespace Enactive.Core.Builds;

/// <summary>How serious a diagnostic is. A warning turned into an error is a new error.</summary>
public enum DiagnosticSeverity { Error, Warning }

/// <summary>
/// One diagnostic a build reported. Not to be confused with Enactive.Core.Diagnostics, which is the
/// application's own logging.
///
/// <para><see cref="Line"/> and <see cref="Column"/> are for a person reading it and are NOT part of
/// its <see cref="Identity"/>. Editing a file moves every diagnostic below the edit; a compiler error
/// that has shifted three lines down is the same error it was before, and treating it as new would
/// report a regression in every step that touched a file with an error already in it.</para>
/// </summary>
/// <param name="Path">Workspace-relative with forward slashes, or null for one with no file, such as
/// <c>MSBUILD : error MSB1009</c>.</param>
public sealed record BuildDiagnostic(
    string Provider,
    string? Path,
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    int? Line = null,
    int? Column = null)
{
    public DiagnosticIdentity Identity => new(Provider, Path, Code, Severity, Message);
}

/// <summary>What makes two diagnostics the same one, whatever line it has moved to.</summary>
public sealed record DiagnosticIdentity(
    string Provider, string? Path, string Code, DiagnosticSeverity Severity, string Message);

/// <summary>A diagnostic identity that occurs more often now than it did before, and by how much.</summary>
public sealed record DiagnosticRegression(DiagnosticIdentity Identity, int Added, BuildDiagnostic Example);

/// <summary>
/// Diagnostics counted by identity - a MULTISET, not a set.
///
/// <para><b>Why a count.</b> Without line numbers, two occurrences of one error in one file - the same
/// missing name used on two lines - have the same identity. As a set they collapse to one, and a step
/// that adds a second occurrence of an error already present would show no regression at all. Counted,
/// that second occurrence is one more than the baseline had, which is exactly what it is.</para>
///
/// <para><b>Why duplicates go first.</b> A build prints each diagnostic more than once, and not by
/// accident. Measured on this machine, 2026-09-28: MSBuild prints every diagnostic once while building
/// and again in its closing summary, and a project with two target frameworks reports it once per
/// framework - one error in the source, four lines in the output. Those are one diagnostic, so exact
/// repeats, position included, are removed before anything is counted. The per-framework suffix is off
/// the message by then, which is what makes the four exact repeats of each other.</para>
/// </summary>
public sealed class DiagnosticSet
{
    private readonly Dictionary<DiagnosticIdentity, int> _counts = new();
    private readonly List<BuildDiagnostic> _all = new();
    private readonly Dictionary<DiagnosticIdentity, BuildDiagnostic> _examples = new();

    private DiagnosticSet() { }

    public static DiagnosticSet Of(IEnumerable<BuildDiagnostic> diagnostics)
    {
        var set = new DiagnosticSet();
        foreach (var diagnostic in diagnostics.Distinct())
        {
            set._all.Add(diagnostic);
            var id = diagnostic.Identity;
            set._counts[id] = set._counts.GetValueOrDefault(id) + 1;
            set._examples.TryAdd(id, diagnostic);
        }
        return set;
    }

    /// <summary>Every diagnostic, repeats removed - what <see cref="Of"/> makes this set again from.</summary>
    public IReadOnlyList<BuildDiagnostic> All => _all;

    public int Count(DiagnosticIdentity identity) => _counts.GetValueOrDefault(identity);

    /// <summary>How many distinct diagnostics of this severity there are, repeats removed.</summary>
    public int Total(DiagnosticSeverity severity)
        => _counts.Where(p => p.Key.Severity == severity).Sum(p => p.Value);

    /// <summary>
    /// What this set has more of than <paramref name="baseline"/>, at this severity - the regressions.
    /// A diagnostic that has only moved is not among them; one that has gone is not either, because
    /// fixing something is not a regression.
    /// </summary>
    public IReadOnlyList<DiagnosticRegression> NewSince(DiagnosticSet baseline,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
        => _counts
            .Where(p => p.Key.Severity == severity && p.Value > baseline.Count(p.Key))
            .Select(p => new DiagnosticRegression(p.Key, p.Value - baseline.Count(p.Key), _examples[p.Key]))
            .OrderBy(r => r.Identity.Path, StringComparer.Ordinal)
            .ThenBy(r => r.Identity.Code, StringComparer.Ordinal)
            .ToArray();
}
