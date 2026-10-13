namespace Enactive.Engine.Tests;

using System.Globalization;
using System.Text.RegularExpressions;
using Enactive.Core;
using Xunit;

/// <summary>
/// The engine's own words - what the model is told, the journal, the reviewer's evidence, the log - write numbers the same
/// on every machine. They were written in the machine's format: "4 200" under a format with a space between thousands,
/// "4,200" under en-US, so a run's record, its prompts and the tests that read them depended on the regional settings of
/// whoever ran it - and a service under its own account wrote them differently from the desktop of the same person.
/// What a person reads in the window (Enactive.App.Ui, the projections) stays in their own format.
/// </summary>
public sealed class EngineNumbersAreInvariantTests
{
    /// <summary>A hole formatted in the machine's culture: "{n:N0}", "{n,9:N0}", "{x:F1}", "{x:0.#}".</summary>
    private static readonly Regex CultureHole = new(@"\{[^{}""]+:(N\d|F\d|0\.[0#]+|#,##0)\}", RegexOptions.Compiled);

    private static readonly Regex CultureToString = new(@"\.ToString\(""(N\d|F\d|0\.[0#]+)""\)", RegexOptions.Compiled);

    [Fact]
    public void The_engine_formats_no_number_in_the_machine_s_culture()
    {
        var src = Path.Combine(TestRepository.Root, "src");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        // What a person reads in the window: in their own format, on purpose.
                        && !f.Contains($"{Path.DirectorySeparatorChar}Enactive.App.Ui{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Projections{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(src, f), Line: i + 1, Text: line)))
            .Where(l => (CultureHole.IsMatch(l.Text) || CultureToString.IsMatch(l.Text))
                        && !l.Text.Contains("InvariantCulture", StringComparison.Ordinal))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Formatted in the machine's culture - use Invariant.N0 / Invariant.Format:\n" + string.Join("\n", offenders));
    }

    /// <summary>The helper itself, under a culture that groups thousands with a space and writes a decimal comma.</summary>
    [Fact]
    public void Invariant_numbers_are_the_same_under_any_culture()
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            Assert.Equal("4,200", Invariant.N0(4200));
            Assert.Equal("1.5", Invariant.Format(1.5, "0.#"));
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }
}
