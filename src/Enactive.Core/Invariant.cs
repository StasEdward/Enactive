namespace Enactive.Core;

using System.Globalization;

/// <summary>
/// Numbers in the engine's own words - what the model is told, the journal, the reviewer's evidence, the log - written the
/// same on every machine. In the machine's format they were "4 200" under one regional setting and "4,200" under another,
/// so a run's prompts and record depended on whoever ran it, and a service under its own account wrote them differently
/// from the desktop. What a person reads in the window keeps their own format; this is for everything else
/// (EngineNumbersAreInvariantTests holds it).
/// </summary>
public static class Invariant
{
    /// <summary>Grouped thousands, no decimals: "4,200".</summary>
    public static string N0<T>(T value) where T : IFormattable => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Any numeric format - "0.#", "F1" - with a decimal point.</summary>
    public static string Format<T>(T value, string format) where T : IFormattable => value.ToString(format, CultureInfo.InvariantCulture);
}
