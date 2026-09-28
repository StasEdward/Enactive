namespace Enactive.Agents;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// asks for is one claim: a refusal that names claims - where an error is, in an error's text, or as
/// missing - gets exactly those claims back, by their stable IDs and with what was wrong with each;
/// the others stand in their places; the whole answer is validated again after the merge.</para>
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

    // Claims asked for one by one. Null: the whole section, when it is asked for at all.
    private readonly ClaimPlan? _claims;

    public IReadOnlyList<string> Requested { get; }
    public IReadOnlyList<string> Kept { get; }
    public string Schema { get; }

    /// <summary>The obligation IDs whose claims are asked for, when only some are; null when the whole section is.</summary>
    public IReadOnlyList<string>? ClaimIds => _claims?.Ids;

    /// <summary>The obligation IDs of the claims in the merged answer, position by position; null when the whole section is asked for.</summary>
    public IReadOnlyList<string>? ClaimOrder => _claims?.Order;

    /// <summary>
    /// Which claims are asked for, the order the merged list takes, and what was wrong with each.
    /// <para>The order is the answer's own: a claim keeps its position, because the repairs and the
    /// reviewer's own reasoning name claims by it. A claim under an ID no obligation has, or a second
    /// one under an ID already answered, is dropped - it answers nothing - and a claim that was
    /// missing is added at the end.</para>
    /// </summary>
    private sealed record ClaimPlan(IReadOnlyList<string> Ids, IReadOnlyList<string> Order,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Errors);

    private ReviewSectionRepair(JsonObject original, IReadOnlyList<string> requested, ClaimPlan? claims = null)
    {
        _original = original;
        _claims = claims;
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
            // An error in one section about another - a repair that points at a claim which did not
            // fail - is a disagreement between the two, and either side may be the one to correct:
            // both are asked for, so the answer can be reconciled rather than patched on one side.
            foreach (Match reference in Reference.Matches(error[(2 + (end < 0 ? path.Length : end))..]))
                if (Sections.Contains(reference.Groups[1].Value)) wrong.Add(reference.Groups[1].Value);
        }
        if (wrong.Count == 0) return null;

        wrong.Add("verdict");
        wrong.Add("notes");
        if (wrong.Overlaps(Findings)) wrong.Add("repairs");
        var requested = Sections.Where(wrong.Contains).ToArray();
        if (requested.Length == Sections.Length) return null;

        var claims = wrong.Contains("claims") && obligations is not null ? PlanClaims(original, errors, obligations) : null;
        return new ReviewSectionRepair(original, requested, claims);
    }

    /// <summary>A path into a section, inside an error's text: <c>$.claims[1]</c>, <c>$.assessments.report</c>.</summary>
    private static readonly Regex Reference = new(@"\$\.([a-z_]+)", RegexOptions.CultureInvariant);

    private static readonly Regex ClaimAt = new(@"\$\.claims\[(\d+)\]", RegexOptions.CultureInvariant);

    private static string? IdOf(JsonNode? claim) => claim is JsonObject o && o["id"]?.GetValueKind() == JsonValueKind.String
        ? o["id"]!.GetValue<string>() : null;

    /// <summary>The positions of the claims that answer an obligation - the first under each known ID - with their IDs.</summary>
    private static List<(int Index, string Id)> Answering(JsonArray claims, IReadOnlyCollection<string> expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<(int, string)>();
        for (var i = 0; i < claims.Count; i++)
            if (IdOf(claims[i]) is { } id && expected.Contains(id) && seen.Add(id))
                kept.Add((i, id));
        return kept;
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
        ClaimPlan? plan = null;
        if (kept["claims"] is JsonArray claims)
        {
            var expected = obligations.Items.Select(o => o.Id).ToArray();
            var answering = Answering(claims, expected);
            var missing = expected.Where(id => answering.All(a => a.Id != id)).ToArray();
            if (missing.Length > 0)
            {
                wrong.Add("claims");
                plan = new ClaimPlan(missing, [.. answering.Select(a => a.Id), .. missing],
                    missing.ToDictionary(id => id, _ => (IReadOnlyList<string>)["not given before the output limit cut the answer"], StringComparer.Ordinal));
            }
        }
        if (wrong.Overlaps(Findings)) wrong.Add("repairs");
        var requested = Sections.Where(wrong.Contains).ToArray();
        return requested.Length == Sections.Length
            ? null
            : new ReviewSectionRepair(kept, requested, plan);
    }

    /// <summary>
    /// The claims a refusal names, by obligation ID - wherever it names them: as the place an error is,
    /// in the text of an error elsewhere, or as missing. Null when it names the section as a whole (not
    /// a list, an error at no claim), or every claim anyway.
    /// </summary>
    private static ClaimPlan? PlanClaims(JsonObject original, IReadOnlyList<string> errors, RequestObligations obligations)
    {
        if (original["claims"] is not JsonArray claims) return null;
        var expected = obligations.Items.Select(o => o.Id).ToArray();
        var answering = Answering(claims, expected);
        var idAt = answering.ToDictionary(a => a.Index, a => a.Id);
        var named = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Name(string id, string error)
        {
            if (!named.TryGetValue(id, out var list)) named[id] = list = [];
            list.Add(error);
        }

        foreach (var error in errors)
        {
            const string missing = "$.claims: missing obligation ID '";
            if (error.StartsWith(missing, StringComparison.Ordinal) && error.IndexOf('\'', missing.Length) is var close and > 0)
            {
                Name(error[missing.Length..close], error);
                continue;
            }
            if (error.StartsWith("$.claims", StringComparison.Ordinal) && !error.StartsWith("$.claims[", StringComparison.Ordinal))
                return null;
            foreach (Match at in ClaimAt.Matches(error))
            {
                if (!int.TryParse(at.Groups[1].Value, out var index) || index >= claims.Count) return null;
                // A claim that answers no obligation is dropped in the merge; the obligation it should
                // have answered is then missing, and named on its own.
                if (idAt.TryGetValue(index, out var id)) Name(id, error);
            }
        }

        var ids = expected.Where(named.ContainsKey).ToArray();
        if (ids.Length == 0 || ids.Length == expected.Length) return null;
        var order = answering.Select(a => a.Id).ToList();
        order.AddRange(ids.Where(id => !order.Contains(id)));
        return new ClaimPlan(ids, order, ids.ToDictionary(id => id, id => (IReadOnlyList<string>)named[id], StringComparer.Ordinal));
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
        var claims = _claims is not { } plan ? "" :
            $" In claims return ONLY the claims for {string.Join(", ", plan.Ids)}, each under its own id; any other id is "
            + $"refused. The claims for {string.Join(", ", plan.Order.Where(id => !plan.Ids.Contains(id)))} stand as you gave them. "
            + $"What is wrong, by claim: {string.Join(" ", plan.Ids.Select(id => $"{id}: {string.Join("; ", plan.Errors[id])}."))} "
            + $"After the merge the claims are in this order: {string.Join(", ", plan.Order.Select((id, i) => $"$.claims[{i}]={id}"))}.";
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
            merged[key] = key == "claims" && _claims is { } plan
                ? MergeClaims(plan, value as JsonArray ?? throw new FormatException("claims: expected the list of the claims asked for."))
                : value?.DeepClone();
        return merged.ToJsonString();
    }

    /// <summary>
    /// The claims asked for, each in the place of the one it corrects, the others as they were given.
    /// Strict, because a claim is matched by its ID alone: an ID not asked for, one sent twice or one
    /// asked for and not sent back would each leave the engine to guess which claim is meant.
    /// </summary>
    private JsonArray MergeClaims(ClaimPlan plan, JsonArray given)
    {
        var byId = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var claim in given)
        {
            if (IdOf(claim) is not { } id)
                throw new FormatException("claims: every entry must be a claim with its id.");
            if (!plan.Ids.Contains(id))
                throw new FormatException($"claims: {id} was not asked for; return only the claims for {string.Join(", ", plan.Ids)}.");
            if (!byId.TryAdd(id, claim!))
                throw new FormatException($"claims: {id} came back twice; return each claim asked for once.");
        }
        var absent = plan.Ids.Where(id => !byId.ContainsKey(id)).ToArray();
        if (absent.Length > 0)
            throw new FormatException($"claims: {string.Join(", ", absent)} did not come back; return every claim asked for.");

        var old = Answering(_original["claims"] as JsonArray ?? [], plan.Order).ToDictionary(a => a.Id, a => a.Index);
        var merged = new JsonArray();
        foreach (var id in plan.Order)
            merged.Add(byId.TryGetValue(id, out var fresh) ? fresh.DeepClone() : _original["claims"]![old[id]]!.DeepClone());
        return merged;
    }
}
