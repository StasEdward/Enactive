namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>Grounded repair targets, not free-form reviewer notes passed straight to the worker.</summary>
internal static class ReviewRepairContract
{
    internal const string Guidance = """
        Return repairs=[] when no concrete correction is needed. Otherwise repairs contains
        {findings:[JSON paths],target,evidence_id,defect,change,obligation_ids}.
        Each fail in assessments, report_checks or a requirement's verification MUST be covered by a repair.
        findings use exact paths such as $.assessments.report, $.report_checks[0],
        $.claims[0].requirements[0].verification. Reference only fail findings. Group duplicate findings
        for the same correction; do not repeat the general assessment as another repair.
        target=source selects an existing worker-report or saved-file fragment; the engine resolves its label and text.
        A wrong assertion in the worker message requires a corrected reply, not an edit to a test file.
        For a missing deliverable or implementation gap with no visible editable fragment use target=work,
        evidence_id=0; state the exact deliverable in change.
        defect states what is wrong; change states the necessary correction (not "review", "look again" or a no-op).
        obligation_ids must identify relevant original source units. Do not rewrite those units.
        Before returning, reconcile verdict, reason, defect and change: if a label is correct, use pass and remove
        its repair. Never retain fail "for completeness". If you cannot identify an actual correction, use unknown
        rather than instructing the worker to hunt for a defect. One behavior can validly refer to several source units:
        do not reject a correct ID merely because another ID also applies or provides its underlying rationale.
        Pass/unknown/not-applicable findings must not request changes. All unknown evidence stays with review.
        
        """;

    private static JsonDocument Parse(string answer) =>
        JsonDocument.Parse(ModelText.ExtractJsonObject(ModelText.StripThink(answer))!);

    private static Dictionary<string, JsonElement> Findings(JsonElement root)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var area in root.GetProperty("assessments").EnumerateObject())
            result.Add("$.assessments." + area.Name, area.Value);
        var i = 0;
        foreach (var report in root.GetProperty("report_checks").EnumerateArray())
            result.Add($"$.report_checks[{i++}]", report);
        var c = 0;
        foreach (var claim in root.GetProperty("claims").EnumerateArray())
        {
            var r = 0;
            foreach (var requirement in claim.GetProperty("requirements").EnumerateArray())
                result.Add($"$.claims[{c}].requirements[{r++}].verification", requirement.GetProperty("verification"));
            c++;
        }
        return result;
    }

    internal static IReadOnlyList<string> Errors(string answer, ReviewSources sources, RequestObligations obligations)
    {
        using var doc = Parse(answer);
        var findings = Findings(doc.RootElement);
        var errors = new List<string>();
        var covered = new HashSet<string>();
        var i = 0;
        foreach (var repair in doc.RootElement.GetProperty("repairs").EnumerateArray())
        {
            var path = $"$.repairs[{i++}]";
            var links = repair.GetProperty("findings").EnumerateArray().Select(x => x.GetString()!).ToArray();
            if (links.Length == 0) errors.Add(path + ".findings: identify at least one failed finding");
            foreach (var link in links)
            {
                if (!findings.TryGetValue(link, out var finding) || finding.GetProperty("verdict").GetString() != "fail")
                    errors.Add(path + ".findings: " + link + " is not a failed finding; reconcile the verdict and proposed correction");
                else if (!covered.Add(link))
                    errors.Add(path + ".findings: duplicate repair for " + link + "; group its correction once");
            }
            foreach (var field in new[] { "defect", "change" })
                if (string.IsNullOrWhiteSpace(repair.GetProperty(field).GetString()))
                    errors.Add(path + "." + field + ": concrete defect and required correction cannot be blank");
            var ids = repair.GetProperty("obligation_ids").EnumerateArray().Select(x => x.GetString()).ToArray();
            if (ids.Length == 0 || ids.Any(id => !obligations.Items.Any(o => o.Id == id)))
                errors.Add(path + ".obligation_ids: select existing original requirement IDs");
            var sourceId = repair.GetProperty("source_id").GetString()!;
            var fragment = repair.GetProperty("fragment_id").GetString()!;
            if (repair.GetProperty("target").GetString() == "work")
            {
                if (sourceId != "" || fragment != "") errors.Add(path + ": work target has no source reference; use source for a visible fragment");
            }
            else
            {
                var source = sources.Find(sourceId);
                var text = source?.Fragment(fragment);
                if (source is null || source.Kind == "execution-evidence" || text is null)
                    errors.Add(path + ": select an existing editable saved-file or worker-report fragment");
                else if (repair.GetProperty("change").GetString()!.Trim() == text.Trim())
                    errors.Add(path + ".change: repeats the current fragment unchanged; reconcile fail with the actual required correction");
            }
            // A report finding has an exact target already; do not redirect its correction to code.
            foreach (var link in links)
                if (findings.TryGetValue(link, out var finding) && finding.TryGetProperty("source_id", out var original))
                    if (repair.GetProperty("target").GetString() != "source" || sourceId != original.GetString()
                        || fragment != finding.GetProperty("fragment_id").GetString())
                        errors.Add(path + ": report correction must target the cited report fragment, not another file");
        }
        foreach (var (path, finding) in findings)
            if (finding.GetProperty("verdict").GetString() == "fail" && !covered.Contains(path))
                errors.Add(path + ": fail has no concrete repair; provide its target/defect/change or reconcile the verdict");
        return errors;
    }

    internal static string Render(string answer, ReviewSources sources, RequestObligations obligations)
    {
        using var doc = Parse(answer);
        var referenced = new HashSet<string>();
        var repairs = doc.RootElement.GetProperty("repairs").EnumerateArray().Select(repair => {
            var source = sources.Find(repair.GetProperty("source_id").GetString()!);
            var ids = repair.GetProperty("obligation_ids").EnumerateArray().Select(x => x.GetString()!).ToArray();
            foreach (var id in ids) referenced.Add(id);
            return new {
                target = source is null ? "work" : source.Kind == "worker-report" ? "worker-message" : "file",
                path = source?.Kind == "saved-file" ? source.Label : null,
                current_fragment = source?.Fragment(repair.GetProperty("fragment_id").GetString()!),
                defect = repair.GetProperty("defect").GetString(),
                required_change = repair.GetProperty("change").GetString(),
                obligation_ids = ids
            };
        }).ToArray();
        return "Repair contract (correct only these targets; retain all other work):\n"
            + JsonSerializer.Serialize(repairs)
            + "\nRelevant original requirements (immutable reference, not a file to edit):\n"
            + JsonSerializer.Serialize(obligations.Items.Where(o => referenced.Contains(o.Id)))
            + "\nworker-message means reply with the corrected statement; do not edit a file to fix a chat statement. "
            + "For file targets, use the named path and quoted fragment; fragment positions are excerpt-local, not physical line numbers. "
            + "The original request is above: rereading a source-file header will not retrieve it. "
            + "Do not rerun unchanged successful checks for a wording-only correction.";
    }
}
