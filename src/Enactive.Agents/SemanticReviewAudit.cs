namespace Enactive.Agents;

using System.Text.Json;
using System.Text.RegularExpressions;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>Validates references and routes explicit semantic findings; does not prove semantic completeness.</summary>
internal static partial class SemanticReviewAudit
{
    internal const string Guidance = """
        Every claims[].requirements[] includes verification={verdict,reason,detects,assertions:[{evidence_id}]}.
        Assess test obligations in the CURRENT scope. pass requires references to actual visible assertions in saved files or displayed tool output containing check source,
        and detects must explain which incorrect behavior those assertions catch. Inspect input, expected result and all
        compared elements; do not substitute test names, comments, test counts or a green process for assertions.
        Use fail for a concrete insufficient check and describe its gap; unknown when the assertion source is unavailable.
        Use not-applicable with a scope explanation for requirements that do not require verification in this step.
        No mandatory mutation for every test unless the request requires it. Do not perform tests yourself.
        report_checks must assess factual assertions in saved reports and the worker report with
        {evidence_id,kind,verdict,reason,calls,obligation_ids}.
        kind=observed requires displayed journal evidence of that exact fact, not just the process exit;
        kind=inferred is for an explicitly labelled inference from code, never a claim that a value was printed or measured.
        Reject invented exact outputs when the log contains only a failure label. Distinguish absent evidence (unknown)
        from a concrete false/unsubstantiated statement presented as observed (fail: correct or qualify that statement).
        kind=requirement-map checks every visible O-ID reference against its original source-unit meaning;
        obligation_ids lists every ID on that fragment. Literal examples can be not-applicable with an explanation.
        Do not accept a private reassignment of O-IDs. Other report checks use obligation_ids=[].
        pass/fail/unknown/not-applicable each require a reason. A failure here overrides a general pass.
        Include every factual result assertion, even if no command exit is mentioned. Use [] only when none exist.
        Citation validity is checked in code, but YOU must judge meaning and completeness.
        
        """;

    /// <summary>
    /// The citation and routing errors - of every part that can be read, whatever else is wrong with the
    /// answer. It used to run only once the structure was clean, so its errors surfaced only after a
    /// correction, with no attempt left (run 4f1d97, step 9: report_checks refused after the parts that
    /// had been asked for were fixed). A part too malformed to read is left to the structural check.
    /// </summary>
    internal static IReadOnlyList<string> Errors(string answer, EvidenceView evidence, ReviewSources sources, RequestObligations obligations)
    {
        if (ModelText.ExtractJsonObject(ModelText.StripThink(answer)) is not { } json) return [];
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return []; }
        using var _ = doc;
        var root = doc.RootElement;
        var errors = new List<string>();
        if (root.ValueKind != JsonValueKind.Object) return errors;

        void Reference(JsonElement reference, string path, bool assertion = false)
        {
            if (Str(reference, "source_id") is not { } sourceId || Str(reference, "fragment_id") is not { } fragmentId) return;
            var source = sources.Find(sourceId);
            if (source is null || source.Fragment(fragmentId) is null)
                errors.Add(path + ": cite an existing nonempty displayed source fragment");
            else if (!assertion && source.Kind == "execution-evidence")
                errors.Add(path + ": tool evidence is not a worker/saved report source");
            else if (assertion && source.Kind is not ("saved-file" or "execution-evidence"))
                errors.Add(path + ": a worker summary is not an assertion implementation; cite visible test/check source");
        }
        var ci = 0;
        foreach (var claim in Arr(root, "claims"))
        {
            var ri = 0;
            foreach (var requirement in Arr(claim, "requirements"))
            {
                var path = $"$.claims[{ci}].requirements[{ri++}].verification";
                if (requirement.ValueKind != JsonValueKind.Object || !requirement.TryGetProperty("verification", out var check)
                    || check.ValueKind != JsonValueKind.Object) continue;
                var verdict = Str(check, "verdict");
                if (Str(requirement, "scope") is { } scope && scope != obligations.CurrentScope
                    && !(requirement.TryGetProperty("global", out var global) && global.ValueKind == JsonValueKind.True)
                    && verdict is not null && verdict != "not-applicable")
                    errors.Add(path + ": deferred requirements need not-applicable with a scope explanation; do not repair another step here");
                var assertions = Arr(check, "assertions").ToArray();
                if (verdict == "pass" && (assertions.Length == 0 || string.IsNullOrWhiteSpace(Str(check, "detects"))))
                    errors.Add(path + ": pass requires actual assertion references and the violating behavior they detect");
                var ai = 0;
                foreach (var assertion in assertions) Reference(assertion, path + $".assertions[{ai++}]", true);
            }
            ci++;
        }
        var mappings = new HashSet<(string, string)>();
        var i = 0;
        foreach (var check in Arr(root, "report_checks"))
        {
            var path = $"$.report_checks[{i++}]";
            if (check.ValueKind != JsonValueKind.Object) continue;
            Reference(check, path);
            foreach (var call in Arr(check, "calls"))
                if (call.ValueKind == JsonValueKind.Number && call.TryGetInt32(out var id) && !evidence.VisibleActionIds.Contains(id))
                    errors.Add(path + ".calls: cite only displayed evidence");
            var kind = Str(check, "kind");
            if (kind == "observed" && Str(check, "verdict") == "pass" && !Arr(check, "calls").Any())
                errors.Add(path + ".calls: an observed result requires visible supporting evidence");
            if (kind != "requirement-map" || Str(check, "source_id") is not { } sourceId || Str(check, "fragment_id") is not { } fragmentId) continue;
            mappings.Add((sourceId, fragmentId));
            var fragment = sources.Find(sourceId)?.Fragment(fragmentId) ?? "";
            var mentioned = Ids(fragment);
            var listed = Arr(check, "obligation_ids").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet();
            if (!mentioned.SetEquals(listed))
                errors.Add(path + ".obligation_ids: list exactly the O-IDs on the cited fragment");
            // Unknown IDs in a report are a WORK defect, not a malformed reviewer response.
            if (listed.Any(id => !obligations.Items.Any(o => o.Id == id)) && Str(check, "verdict") == "pass")
                errors.Add(path + ".verdict: unknown report O-ID cannot pass; assess the report defect");
        }
        // Only when report_checks could be read at all: a missing section is the structural check's to name.
        if (root.TryGetProperty("report_checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
            foreach (var source in sources.All.Where(s => s.Kind != "execution-evidence"))
                for (var f = 0; f < source.Fragments.Length; f++)
                    if (Ids(source.Fragments[f]).Count > 0 && !mappings.Contains((source.Id, $"F{f + 1}")))
                        errors.Add($"$.report_checks: missing requirement-map assessment for {source.Id}/F{f + 1}");
        return errors;
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().ToArray() : [];

    internal static HashSet<string> Ids(string text) => Regex.Matches(text, @"\bO\d{3,}\b", RegexOptions.None,
        TimeSpan.FromMilliseconds(100)).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    internal sealed record Finding(bool Unknown, string Reason);
    /// <summary>
    /// The calls the reviewer named for what it could not establish - the "calls" of every report check and
    /// verification whose verdict is unknown - when there is no concrete failure beside them.
    /// </summary>
    /// <param name="evidence">
    /// Where an unknown names no call but names a FILE, the shown calls whose arguments name that file stand for it.
    /// Run 341c2f, 2026-09-29: "the coverage figures come from coverage-report.md, whose numeric portion was cut" -
    /// with calls: [] - and the read of that file was in the evidence, cut.
    /// </param>
    internal static IReadOnlyList<int> UnknownCalls(string answer, EvidenceView? evidence = null)
    {
        if (ModelText.ExtractJsonObject(ModelText.StripThink(answer)) is not { } json) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var unknown = Arr(root, "report_checks")
                .Concat(Arr(root, "claims").SelectMany(c => Arr(c, "requirements"))
                    .Select(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("verification", out var v) ? v : default))
                .Concat(root.TryGetProperty("assessments", out var a) && a.ValueKind == JsonValueKind.Object
                    ? a.EnumerateObject().Select(p => p.Value) : [])
                .Where(c => c.ValueKind == JsonValueKind.Object && Str(c, "verdict") == "unknown").ToArray();
            var named = unknown.SelectMany(c => Arr(c, "calls")).Where(id => id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out _))
                .Select(id => id.GetInt32()).Distinct().ToArray();
            if (named.Length > 0 || evidence is null) return named;
            var files = unknown.Select(c => Str(c, "reason") ?? "")
                .SelectMany(reason => FileName().Matches(reason).Select(m => m.Value.Replace('\\', '/')))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return files.Length == 0 ? []
                : evidence.VisibleActionIds.OrderBy(id => id)
                    .Where(id => evidence.Cited(id) is { } call
                                 && files.Any(f => call.Arguments.Replace("\\\\", "/").Replace('\\', '/').Contains(f, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
        }
        catch (JsonException) { return []; }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[A-Za-z0-9_.\-/\\]*[A-Za-z0-9_\-]\.[A-Za-z][A-Za-z0-9]{0,7}\b")]
    private static partial System.Text.RegularExpressions.Regex FileName();

    /// <summary>
    /// What the reviewer could not establish, as items - the label, its reason, and the calls it named described as
    /// the calls themselves (tool and arguments), because their numbers belong to this review's evidence and mean
    /// nothing in another (Phase 9: the task review is shown them as open questions).
    /// </summary>
    internal static IReadOnlyList<OpenItem> UnknownItems(string answer, EvidenceView evidence, int step, string stepTitle)
    {
        if (ModelText.ExtractJsonObject(ModelText.StripThink(answer)) is not { } json) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var found = Arr(root, "claims").SelectMany(c => Arr(c, "requirements"))
                    .Select(r => (Label: Str(r, "requirement"), Check: r.ValueKind == JsonValueKind.Object && r.TryGetProperty("verification", out var v) ? v : default))
                .Concat(Arr(root, "report_checks").Select(c => (Label: (string?)("Report " + Str(c, "source_id") + "/" + Str(c, "fragment_id")), Check: c)))
                .Concat(root.TryGetProperty("assessments", out var a) && a.ValueKind == JsonValueKind.Object
                    ? a.EnumerateObject().Select(p => (Label: (string?)p.Name, Check: p.Value)) : [])
                .Where(c => c.Check.ValueKind == JsonValueKind.Object && Str(c.Check, "verdict") == "unknown");
            return found.Select(c => new OpenItem(step, stepTitle, c.Label ?? "unlabelled", Str(c.Check, "reason") ?? "",
                    Arr(c.Check, "calls").Where(id => id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out _))
                        .Select(id => evidence.Cited(id.GetInt32())).OfType<ExecutedAction>()
                        .Select(x => Clip($"{x.Tool} {x.Arguments}", 200)).ToArray()))
                .ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    internal static Finding? Outcome(string answer)
    {
        using var doc = JsonDocument.Parse(ModelText.ExtractJsonObject(ModelText.StripThink(answer))!);
        var checks = doc.RootElement.GetProperty("claims").EnumerateArray()
            .SelectMany(c => c.GetProperty("requirements").EnumerateArray()
                .Select(r => (Label: r.GetProperty("requirement").GetString(), Check: r.GetProperty("verification"))))
            .Concat(doc.RootElement.GetProperty("report_checks").EnumerateArray()
                .Select(c => (Label: (string?)("Report " + c.GetProperty("source_id").GetString() + "/" + c.GetProperty("fragment_id").GetString()), Check: c)))
            .Concat(doc.RootElement.GetProperty("assessments").EnumerateObject()
                .Select(a => (Label: (string?)a.Name, Check: a.Value))).ToArray();
        string Lines(IEnumerable<(string? Label, JsonElement Check)> found)
            => string.Join("\n", found.Select(c => c.Label + ": " + c.Check.GetProperty("reason").GetString()));
        var failed = checks.Where(c => c.Check.GetProperty("verdict").GetString() == "fail").ToArray();
        var unknown = checks.Where(c => c.Check.GetProperty("verdict").GetString() == "unknown").ToArray();
        // A concrete failure is a verdict, whatever else could not be told. It used to be the other
        // way round, and on 2026-09-28 one "unknown" beside a found violation - a source file changed
        // against an explicit ban - turned the whole final review into "no verdict". Unknown still
        // never becomes a worker repair: the repair contract renders failures only, and what could
        // not be established is named here, for the reader, and stays with review.
        if (failed.Length > 0)
            return new(false, Lines(failed)
                + (unknown.Length == 0 ? "" : "\nNot established (left with review, not a repair):\n" + Lines(unknown)));
        return unknown.Length > 0 ? new(true, Lines(unknown)) : null;
    }
}
