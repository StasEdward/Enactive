namespace Enactive.Agents;

using System.Text.Json;
using System.Text.RegularExpressions;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>Validates references and routes explicit semantic findings; does not prove semantic completeness.</summary>
internal static class SemanticReviewAudit
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

    internal static IReadOnlyList<string> Errors(string answer, EvidenceView evidence, ReviewSources sources, RequestObligations obligations)
    {
        using var doc = JsonDocument.Parse(ModelText.ExtractJsonObject(ModelText.StripThink(answer))!);
        var root = doc.RootElement;
        var errors = new List<string>();
        void Reference(JsonElement reference, string path, bool assertion = false)
        {
            var source = sources.Find(reference.GetProperty("source_id").GetString()!);
            if (source is null || source.Fragment(reference.GetProperty("fragment_id").GetString()!) is null)
                errors.Add(path + ": cite an existing nonempty displayed source fragment");
            else if (!assertion && source.Kind == "execution-evidence")
                errors.Add(path + ": tool evidence is not a worker/saved report source");
            else if (assertion && source.Kind is not ("saved-file" or "execution-evidence"))
                errors.Add(path + ": a worker summary is not an assertion implementation; cite visible test/check source");
        }
        var ci = 0;
        foreach (var claim in root.GetProperty("claims").EnumerateArray())
        {
            var ri = 0;
            foreach (var requirement in claim.GetProperty("requirements").EnumerateArray())
            {
                var path = $"$.claims[{ci}].requirements[{ri++}].verification";
                var check = requirement.GetProperty("verification");
                var assertions = check.GetProperty("assertions");
                if (requirement.GetProperty("scope").GetString() != obligations.CurrentScope
                    && !requirement.GetProperty("global").GetBoolean()
                    && check.GetProperty("verdict").GetString() != "not-applicable")
                    errors.Add(path + ": deferred requirements need not-applicable with a scope explanation; do not repair another step here");
                if (check.GetProperty("verdict").GetString() == "pass"
                    && (assertions.GetArrayLength() == 0 || string.IsNullOrWhiteSpace(check.GetProperty("detects").GetString())))
                    errors.Add(path + ": pass requires actual assertion references and the violating behavior they detect");
                var ai = 0;
                foreach (var assertion in assertions.EnumerateArray()) Reference(assertion, path + $".assertions[{ai++}]", true);
            }
            ci++;
        }
        var mappings = new HashSet<(string, string)>();
        var i = 0;
        foreach (var check in root.GetProperty("report_checks").EnumerateArray())
        {
            var path = $"$.report_checks[{i++}]";
            Reference(check, path);
            foreach (var call in check.GetProperty("calls").EnumerateArray())
                if (!evidence.VisibleActionIds.Contains(call.GetInt32()))
                    errors.Add(path + ".calls: cite only displayed evidence");
            var kind = check.GetProperty("kind").GetString();
            if (kind == "observed" && check.GetProperty("verdict").GetString() == "pass"
                && check.GetProperty("calls").GetArrayLength() == 0)
                errors.Add(path + ".calls: an observed result requires visible supporting evidence");
            if (kind != "requirement-map") continue;
            var sourceId = check.GetProperty("source_id").GetString()!;
            var fragmentId = check.GetProperty("fragment_id").GetString()!;
            mappings.Add((sourceId, fragmentId));
            var fragment = sources.Find(sourceId)?.Fragment(fragmentId) ?? "";
            var mentioned = Ids(fragment);
            var listed = check.GetProperty("obligation_ids").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
            if (!mentioned.SetEquals(listed))
                errors.Add(path + ".obligation_ids: list exactly the O-IDs on the cited fragment");
            // Unknown IDs in a report are a WORK defect, not a malformed reviewer response.
            if (listed.Any(id => !obligations.Items.Any(o => o.Id == id))
                && check.GetProperty("verdict").GetString() == "pass")
                errors.Add(path + ".verdict: unknown report O-ID cannot pass; assess the report defect");
        }
        foreach (var source in sources.All.Where(s => s.Kind != "execution-evidence"))
            for (var f = 0; f < source.Fragments.Length; f++)
                if (Ids(source.Fragments[f]).Count > 0 && !mappings.Contains((source.Id, $"F{f + 1}")))
                    errors.Add($"$.report_checks: missing requirement-map assessment for {source.Id}/F{f + 1}");
        return errors;
    }

    internal static HashSet<string> Ids(string text) => Regex.Matches(text, @"\bO\d{3,}\b", RegexOptions.None,
        TimeSpan.FromMilliseconds(100)).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    internal sealed record Finding(bool Unknown, string Reason);
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
        // Unknown must not cause an invented worker repair even if other findings are concrete.
        foreach (var verdict in new[] { "unknown", "fail" })
        {
            var found = checks.Where(c => c.Check.GetProperty("verdict").GetString() == verdict).ToArray();
            if (found.Length > 0) return new(verdict == "unknown",
                string.Join("\n", found.Select(c => c.Label + ": " + c.Check.GetProperty("reason").GetString())));
        }
        return null;
    }
}
