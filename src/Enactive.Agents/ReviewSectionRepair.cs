namespace Enactive.Agents;

using System.Text.Json;
using System.Text.Json.Nodes;
using Enactive.Core.Execution;
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
///
/// <para><b>Claims one by one.</b> The claims are the largest section - one per obligation, each
/// with its requirements - and where the recorded refusals mostly are: a missing ID, a wrong scope,
/// two fields missing from one requirement of one claim. Asking for the whole section again for that
/// re-generated every claim that was right. The engine owns the list of obligations, so the unit it
/// asks for is one claim: a refusal that names claims by position or a missing ID gets exactly those
/// claims back, the others stand, and the merged list is put in the obligations' order.</para>
///
/// <para><b>A cut answer keeps what was complete.</b> An answer stopped by the output limit was asked
/// for again whole, with twice the room. Everything before the cut that closed - whole sections, whole
/// claims - is kept instead, and only what is missing is asked for, which is smaller than the answer
/// that did not fit.</para>
/// </summary>
internal sealed class ReviewSectionRepair
{
    /// <summary>The top-level sections of a combined review, in the schema's order.</summary>
    internal static readonly string[] Sections =
        ["verdict", "notes", "assessments", "proof", "claims", "need_evidence", "command_reports", "report_checks", "repairs"];

    /// <summary>The sections repairs point into.</summary>
    private static readonly HashSet<string> Findings = ["assessments", "claims", "report_checks"];

    private readonly JsonObject _original;

    // Claims asked for one by one: which, and the order the merged list takes. Null: the whole section.
    private readonly IReadOnlyList<string>? _claimIds;
    private readonly IReadOnlyList<string> _order;

    public IReadOnlyList<string> Requested { get; }
    public IReadOnlyList<string> Kept { get; }
    public string Schema { get; }

    /// <summary>The obligation IDs whose claims are asked for, when only some are; null when the whole section is.</summary>
    public IReadOnlyList<string>? ClaimIds => _claimIds;

    private ReviewSectionRepair(JsonObject original, IReadOnlyList<string> requested,
        IReadOnlyList<string>? claimIds = null, IReadOnlyList<string>? order = null)
    {
        _original = original;
        _claimIds = claimIds;
        _order = order ?? [];
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
    /// <param name="obligations">The obligations the claims answer; with them, claims are asked for one by one.</param>
    public static ReviewSectionRepair? For(string decodedAnswer, IReadOnlyList<string> errors, RequestObligations? obligations = null)
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
        if (requested.Length == Sections.Length) return null;

        var claimIds = wrong.Contains("claims") && obligations is not null
            ? ClaimsNamed(original, errors.Where(e => e.StartsWith("$.claims", StringComparison.Ordinal)).ToArray(), obligations)
            : null;
        return new ReviewSectionRepair(original, requested, claimIds, obligations?.Items.Select(o => o.Id).ToArray());
    }

    /// <summary>
    /// What to ask for after an answer was cut by the output limit: its complete parts are kept - whole
    /// sections, whole claims - and the rest is asked for. Null when nothing before the cut was complete,
    /// and the answer is asked for again as before.
    /// </summary>
    /// <param name="decode">Turns the kept parts' evidence numbers into references, as a whole answer's are.</param>
    public static ReviewSectionRepair? ForCut(string rawAnswer, RequestObligations obligations, Func<string, string> decode)
    {
        if (CompletePrefix(ModelText.StripThink(rawAnswer)) is not { } prefix) return null;
        JsonObject kept;
        try { kept = JsonNode.Parse(decode(prefix))!.AsObject(); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { return null; }
        if (!kept.Any(p => Sections.Contains(p.Key) && p.Key is not ("verdict" or "notes"))) return null;
        foreach (var unknown in kept.Select(p => p.Key).Where(k => !Sections.Contains(k)).ToArray()) kept.Remove(unknown);

        var wrong = Sections.Where(s => !kept.ContainsKey(s)).ToHashSet(StringComparer.Ordinal);
        wrong.Add("verdict");
        wrong.Add("notes");
        IReadOnlyList<string>? claimIds = null;
        if (kept["claims"] is JsonArray claims)
        {
            var present = claims.OfType<JsonObject>().Select(c => c["id"]?.GetValueKind() == JsonValueKind.String ? c["id"]!.GetValue<string>() : null)
                .OfType<string>().ToHashSet(StringComparer.Ordinal);
            var missing = obligations.Items.Select(o => o.Id).Where(id => !present.Contains(id)).ToArray();
            if (missing.Length > 0)
            {
                wrong.Add("claims");
                claimIds = missing;
            }
        }
        if (wrong.Overlaps(Findings)) wrong.Add("repairs");
        var requested = Sections.Where(wrong.Contains).ToArray();
        return requested.Length == Sections.Length
            ? null
            : new ReviewSectionRepair(kept, requested, claimIds, obligations.Items.Select(o => o.Id).ToArray());
    }

    /// <summary>
    /// The claims a refusal names, by obligation ID - or null when it names the section as a whole
    /// (not a list, or an error with no claim to place it in), or every claim anyway.
    /// </summary>
    private static IReadOnlyList<string>? ClaimsNamed(JsonObject original, IReadOnlyList<string> errors, RequestObligations obligations)
    {
        if (original["claims"] is not JsonArray claims) return null;
        var expected = obligations.Items.Select(o => o.Id).ToArray();
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var error in errors)
        {
            const string missing = "$.claims: missing obligation ID '";
            if (error.StartsWith(missing, StringComparison.Ordinal) && error.IndexOf('\'', missing.Length) is var close and > 0)
            {
                named.Add(error[missing.Length..close]);
                continue;
            }
            if (!error.StartsWith("$.claims[", StringComparison.Ordinal)) return null;
            var end = error.IndexOf(']');
            if (end < 0 || !int.TryParse(error["$.claims[".Length..end], out var index) || index < 0 || index >= claims.Count) return null;
            // A claim under an ID no obligation has, or a second one under the same ID, is dropped in the
            // merge; the obligation it should have answered is then missing and named on its own.
            if (claims[index] is JsonObject claim && claim["id"]?.GetValueKind() == JsonValueKind.String
                && claim["id"]!.GetValue<string>() is var id && expected.Contains(id)
                && !error.Contains("duplicate obligation ID", StringComparison.Ordinal))
                named.Add(id);
        }
        var ids = expected.Where(named.Contains).ToArray();
        return ids.Length == 0 || ids.Length == expected.Length ? null : ids;
    }

    /// <summary>
    /// The members of a cut JSON object that closed before the cut, as a valid object - with, when the
    /// member that was cut is a list, the items of it that closed. Null when nothing did, or the text
    /// is not a cut object at all (a whole one is not this method's business).
    /// </summary>
    internal static string? CompletePrefix(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escape = false;
        var lastMember = -1;          // the comma after the last complete member
        var memberStart = start + 1;  // where the member being read began
        var memberIsList = false;
        var lastItem = -1;            // the comma after the last complete item of that member's list
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{' or '[':
                    if (depth == 1 && c == '[') memberIsList = true;
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    if (depth == 0) return null;   // the object closed: it was not cut
                    break;
                case ',' when depth == 1:
                    lastMember = i;
                    memberStart = i + 1;
                    memberIsList = false;
                    lastItem = -1;
                    break;
                case ',' when depth == 2 && memberIsList:
                    lastItem = i;
                    break;
            }
        }

        var members = lastMember < 0 ? "" : text[(start + 1)..lastMember];
        if (memberIsList && lastItem > 0)
        {
            // The list being written when the cut came, with the items that closed.
            var partial = text[memberStart..lastItem] + "]";
            members = members.Length == 0 ? partial : members + "," + partial;
        }
        if (members.Trim().Length == 0) return null;
        var candidate = "{" + members + "}";
        try { JsonNode.Parse(candidate); }
        catch (JsonException) { return null; }
        return candidate;
    }

    /// <summary>What the reviewer is asked: exactly these parts, with the rest named as kept.</summary>
    public string Instruction(string diagnostic)
    {
        var claims = _claimIds is null ? "" :
            $" In claims return ONLY the claims for {string.Join(", ", _claimIds)}; the claims for "
            + $"{string.Join(", ", _order.Where(id => !_claimIds.Contains(id)))} stand as you gave them. After the merge the "
            + $"claims are in this order: {string.Join(", ", _order.Select((id, i) => $"$.claims[{i}]={id}"))}.";
        return diagnostic
           + $"\nCorrect ONLY these parts of your answer, and return them as one JSON object with exactly these "
           + $"top-level fields: {string.Join(", ", Requested)}. Everything else you gave stands exactly as it was "
           + $"and is kept by the engine: {string.Join(", ", Kept)}.{claims} Paths in repairs refer to the whole answer "
           + "after your corrected parts replace the old ones. Reassess scope against the request; do not change "
           + "evidence or invent citations to obtain a pass.";
    }

    /// <summary>
    /// The answer with the parts given in place of the old ones. Lenient about shape where leniency
    /// costs nothing - a whole answer sent back instead of parts replaces everything, a known
    /// section sent back unasked replaces its old self, every claim sent back when some were asked
    /// for replaces them all - because everything is validated again after the merge. Not about what
    /// cannot be a part: a field no review has, or no part at all.
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
            merged[key] = key == "claims" && _claimIds is not null && value is JsonArray given
                ? MergeClaims(given)
                : value?.DeepClone();
        return merged.ToJsonString();
    }

    /// <summary>
    /// The claims asked for from <paramref name="given"/>, the rest from the answer they correct, in the
    /// obligations' order. A claim under an ID no obligation has is left out; validation names what is missing.
    /// </summary>
    private JsonArray MergeClaims(JsonArray given)
    {
        static string? Id(JsonNode? claim) => claim is JsonObject o && o["id"]?.GetValueKind() == JsonValueKind.String
            ? o["id"]!.GetValue<string>() : null;
        var byId = given.Where(c => Id(c) is not null).GroupBy(Id).ToDictionary(g => g.Key!, g => g.First(), StringComparer.Ordinal);
        // Every claim came back: the whole section was answered, and it replaces the old one.
        if (_order.All(byId.ContainsKey)) return (JsonArray)given.DeepClone();
        var old = (_original["claims"] as JsonArray ?? []).Where(c => Id(c) is not null)
            .GroupBy(Id).ToDictionary(g => g.Key!, g => g.First(), StringComparer.Ordinal);

        var merged = new JsonArray();
        foreach (var id in _order)
        {
            var claim = _claimIds!.Contains(id) && byId.TryGetValue(id, out var fresh) ? fresh
                : old.TryGetValue(id, out var kept) ? kept
                : byId.GetValueOrDefault(id);
            if (claim is not null) merged.Add(claim.DeepClone());
        }
        return merged;
    }
}
