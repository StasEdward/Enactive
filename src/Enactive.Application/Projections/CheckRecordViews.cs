namespace Enactive.Application.Projections;

using System.Globalization;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.Templates;
using Enactive.Settings;
using Enactive.Workspace;

/// <summary>How one check's line in a check record is drawn.</summary>
internal enum CheckLineTone
{
    Passed,
    Failed,
    NotRun
}

/// <summary>One check's result in a record, as a line: what it is, what it came to, and where its whole output is.</summary>
/// <param name="DetailPath">Workspace-relative, with '/'; null when there was no output to keep.</param>
internal sealed record CheckRecordLine(string Text, CheckLineTone Tone, string? DetailPath);

/// <summary>
/// A "Run checks now" record (Plan Phase 3.2) as the window and the service's pages show it - one shape, so the two say the
/// same words: when, who, what each check came to, and whether the workspace is still in the state that was checked.
/// </summary>
/// <param name="Headline">"Checked at 2026-10-17 14:02 by alice: 1 passed, 1 failed".</param>
/// <param name="State">"Checked at …" while the fingerprint matches, "Outdated: …" once it does not - never a standing green.</param>
/// <param name="Caveat">What a match does not prove (outside git, always); null when nothing needs adding.</param>
/// <param name="Note">What else the record says - the workspace changed while its checks ran; null when nothing.</param>
internal sealed record CheckRecordView(Guid Id, string Headline, string State, bool Outdated,
    IReadOnlyList<CheckRecordLine> Lines, string? Caveat, string? Note);

/// <summary>The words of a check record, and a run's records read with whether each is still current.</summary>
internal static class CheckRecordViews
{
    /// <summary>What the action is, said beside its button - the same on both hosts.</summary>
    public const string Explanation = "Runs the checks this run was started with, under the permissions of now, against the "
        + "workspace as it is now. The result is kept here; it does not change how the run ended.";

    /// <summary>The words for one record; <paramref name="current"/> is whether its fingerprint still matches (ChecksNow.IsCurrentAsync).</summary>
    public static CheckRecordView Of(CheckRecord record, bool current)
    {
        var when = When(record.At);
        // Never a pass pill: the record is "this state was checked at this time" (the plan), and what is true now is only
        // that nothing it fingerprinted has changed - which is not that nothing changed.
        var state = current
            ? $"Checked at {when}: nothing it fingerprinted has changed since."
            : $"Outdated: the workspace has changed since it was checked at {when}.";
        return new(record.Id, $"Checked at {when} by {record.Who}: {Counts(record.Results)}", state, !current,
            record.Results.Select(Line).ToArray(), record.Fingerprint.Caveat, record.Note);
    }

    /// <summary>"2 passed, 1 failed, 1 not run" - only what there is.</summary>
    public static string Counts(IReadOnlyList<CheckResultLine> results)
    {
        var parts = new List<string>();
        void Add(int n, string what) { if (n > 0) parts.Add($"{n.ToString(CultureInfo.InvariantCulture)} {what}"); }
        Add(results.Count(r => r.Outcome == CriterionOutcome.Passed), "passed");
        Add(results.Count(r => r.Outcome == CriterionOutcome.Failed), "failed");
        Add(results.Count(r => r.Outcome == CriterionOutcome.Unknown), "not run");
        return parts.Count == 0 ? "no checks" : string.Join(", ", parts);
    }

    /// <summary>One check: "PASS lint (npm run lint)", "FAIL lint (npm run lint), exit 1", "NOT RUN lint - not run: needs permission - …".</summary>
    public static CheckRecordLine Line(CheckResultLine result)
    {
        var what = $"{result.CheckId}{(result.Blocking ? " [blocking]" : "")} ({result.Command})";
        return result.Outcome switch
        {
            CriterionOutcome.Passed => new($"PASS {what}", CheckLineTone.Passed, result.DetailPath),
            CriterionOutcome.Failed => new($"FAIL {what}" + (result.ExitCode is { } exit ? $", exit {exit.ToString(CultureInfo.InvariantCulture)}" : ""),
                CheckLineTone.Failed, result.DetailPath),
            _ => new($"NOT RUN {what}" + (string.IsNullOrWhiteSpace(result.Detail) ? "" : " - " + result.Detail.Trim()),
                CheckLineTone.NotRun, result.DetailPath)
        };
    }

    /// <summary>
    /// A run's records, newest first, each with whether the workspace still matches it. The fingerprint is taken again once
    /// for every set of inputs, not once per record: in git every record of a run is compared with one state, and a page
    /// listing five records ran git twenty times.
    /// </summary>
    public static async Task<IReadOnlyList<CheckRecordView>> ForRunAsync(string root, Guid runId, CancellationToken ct)
    {
        if (!Directory.Exists(root))
            return [];
        var records = await RunStoreFactory.Create(WorkspaceInfo.For(root)).LoadCheckRecordsAsync(runId, ct);
        var now = new Dictionary<string, StateFingerprint?>(StringComparer.Ordinal);
        var views = new List<CheckRecordView>(records.Count);
        foreach (var record in records.OrderByDescending(r => r.At))
        {
            var key = KeyOf(record.Fingerprint);
            if (!now.TryGetValue(key, out var taken))
                now[key] = taken = await StateFingerprints.AgainAsync(root, record.Fingerprint, KnownEcosystems.All, ct);
            views.Add(Of(record, taken is not null && StateFingerprints.Same(taken, record.Fingerprint)));
        }
        return views;
    }

    /// <summary>What a fingerprint is taken again over: its kind and, outside git, the inputs it kept.</summary>
    private static string KeyOf(StateFingerprint f)
        => f.Kind == FingerprintKind.Files
            ? string.Join("\n", ["files", .. f.Proposed ?? [], "\u0001", .. f.Matches ?? [], "\u0001", .. f.Ecosystems ?? []])
            : f.Kind.ToString();

    private static string When(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
