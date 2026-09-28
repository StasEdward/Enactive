namespace Enactive.Agents;

using System.Text.Json;
using System.Text.Json.Nodes;
using Enactive.Core.Providers;

/// <summary>
/// A refused combined review corrected by PARTS: the sections the refusal names are asked for again,
/// and every other section of the answer stands as it was given.
///
/// <para><b>Why (amendment G).</b> A refusal used to be answered with the whole review again - one
/// structure of five to twenty thousand characters - and the second whole answer was free to break
/// what the first had right. The corpus shows it: of the refusals recorded on 2026-09-28, the second
/// attempt of one final review fixed its call numbers and broke its repairs; another answer lost a
/// field it had carried before. The engine owns the list of what a review must contain; it can ask
/// for the part that is wrong, keep the parts that are not, and validate the whole again.</para>
///
/// <para><b>What goes back together.</b> The verdict and its notes always - a corrected part can
/// change the verdict. The repairs whenever a section they point into is asked for, because a repair
/// names findings by their path in the answer, and a rewritten section moves them. Anything the
/// refusal cannot place in one section - the answer as a whole, a field the schema does not have -
/// is not a part, and the whole answer is asked for as before.</para>
/// </summary>
internal sealed class ReviewSectionRepair
{
    /// <summary>The top-level sections of a combined review, in the schema's order.</summary>
    internal static readonly string[] Sections =
        ["verdict", "notes", "assessments", "proof", "claims", "need_evidence", "command_reports", "report_checks", "repairs"];

    /// <summary>The sections repairs point into.</summary>
    private static readonly HashSet<string> Findings = ["assessments", "claims", "report_checks"];

    private readonly JsonObject _original;

    public IReadOnlyList<string> Requested { get; }
    public IReadOnlyList<string> Kept { get; }
    public string Schema { get; }

    private ReviewSectionRepair(JsonObject original, IReadOnlyList<string> requested)
    {
        _original = original;
        Requested = requested;
        Kept = Sections.Where(s => !requested.Contains(s)).ToArray();
        var full = JsonNode.Parse(Reviewer.CombinedWireSchema)!["properties"]!;
        var properties = new JsonObject();
        foreach (var section in requested) properties[section] = full[section]!.DeepClone();
        Schema = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = properties,
            ["required"] = new JsonArray(requested.Select(r => (JsonNode)r).ToArray())
        }.ToJsonString();
    }

    /// <summary>
    /// The parts to ask for again, from the refusal's own paths - or null when the refusal is not
    /// about parts: there is no whole answer to keep parts of, an error is about the answer as a
    /// whole or a field that has no section, or every section is wrong anyway.
    /// </summary>
    public static ReviewSectionRepair? For(string decodedAnswer, IReadOnlyList<string> errors)
    {
        if (ModelText.ExtractJsonObject(ModelText.StripThink(decodedAnswer)) is not { } json) return null;
        JsonObject original;
        try { original = JsonNode.Parse(json)!.AsObject(); }
        catch (JsonException) { return null; }

        var wrong = new HashSet<string>(StringComparer.Ordinal);
        foreach (var error in errors)
        {
            if (!error.StartsWith("$.", StringComparison.Ordinal)) return null;
            var path = error[2..];
            var end = path.IndexOfAny(['.', '[', ':', ' ']);
            var section = end < 0 ? path : path[..end];
            if (!Sections.Contains(section)) return null;
            wrong.Add(section);
        }
        if (wrong.Count == 0) return null;

        wrong.Add("verdict");
        wrong.Add("notes");
        if (wrong.Overlaps(Findings)) wrong.Add("repairs");
        var requested = Sections.Where(wrong.Contains).ToArray();
        return requested.Length == Sections.Length ? null : new ReviewSectionRepair(original, requested);
    }

    /// <summary>What the reviewer is asked: exactly these parts, with the rest named as kept.</summary>
    public string Instruction(string diagnostic)
        => diagnostic
           + $"\nCorrect ONLY these parts of your answer, and return them as one JSON object with exactly these "
           + $"top-level fields: {string.Join(", ", Requested)}. Everything else you gave stands exactly as it was "
           + $"and is kept by the engine: {string.Join(", ", Kept)}. Paths in repairs refer to the whole answer "
           + "after your corrected parts replace the old ones. Reassess scope against the request; do not change "
           + "evidence or invent citations to obtain a pass.";

    /// <summary>
    /// The answer with the parts given in place of the old ones. Lenient about shape where leniency
    /// costs nothing - a whole answer sent back instead of parts replaces everything, a known
    /// section sent back unasked replaces its old self - because everything is validated again
    /// after the merge. Not about what cannot be a part: a field no review has, or no part at all.
    /// </summary>
    public string Merge(string decodedPatch)
    {
        var json = ModelText.ExtractJsonObject(ModelText.StripThink(decodedPatch))
                   ?? throw new FormatException("The corrected parts must be one JSON object"
                       + (ModelText.JsonProblem(ModelText.StripThink(decodedPatch)) is { } broken ? ": " + broken : "."));
        var patch = JsonNode.Parse(json)!.AsObject();
        var unknown = patch.Select(p => p.Key).Where(k => !Sections.Contains(k)).ToArray();
        if (unknown.Length > 0)
            throw new FormatException($"Return only the parts asked for ({string.Join(", ", Requested)}); "
                + $"a review has no field {string.Join(", ", unknown)}.");
        if (!patch.Any(p => Requested.Contains(p.Key)))
            throw new FormatException($"None of the parts asked for came back: {string.Join(", ", Requested)}.");

        var merged = _original.DeepClone().AsObject();
        foreach (var (key, value) in patch)
            merged[key] = value?.DeepClone();
        return merged.ToJsonString();
    }
}
