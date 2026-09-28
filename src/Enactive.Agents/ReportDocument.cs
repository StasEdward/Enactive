namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using Enactive.Core.Tasks;

/// <summary>
/// The document a step done for each item declares as its <c>report</c>, written by code from what the
/// engine recorded - every heading, status, count and table - with the model's part confined to the
/// summary it hands on as a value.
///
/// <para><b>Why.</b> Run 4f1d97, 2026-09-28: twelve steps editing one report left it with duplicated
/// tables, clashing numbering and rows each step had marked "Reviewed" before any review. Here nothing
/// in the document is a step's say about itself: a status comes from the step's recorded outcome, and
/// findings that are less than confirmed are shown as what they are. Rendered again from the records
/// whenever they change and at the end of the run, so a resumed run writes the same document.</para>
///
/// <para>It shows the state of the checking, not the truth of the findings: a confirmed result is one
/// a reviewer passed, which is not proof that every finding in it is right.</para>
/// </summary>
internal static class ReportDocument
{
    /// <summary>The name of the value a step after the items hands on to be the document's summary.</summary>
    internal const string SummaryField = "summary";

    internal sealed record Item(IReadOnlyList<string> Names, StepRecord? Record);

    internal sealed record Summary(string Step, string Text, StepRecord? Record);

    /// <param name="notChecked">Why no item was checked at all, when none was - the items were never listed, or not given steps.</param>
    public static string Render(string title, IReadOnlyList<Item> items, IReadOnlyList<Summary> summaries, string? notChecked = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}").AppendLine();
        if (notChecked is not null)
            sb.AppendLine($"**No item was checked: {notChecked}.**").AppendLine();
        sb.AppendLine("_Assembled by the engine from each item's recorded result. Statuses are the engine's - from how each "
                      + "step ended and what its review found - not the steps' own; a confirmed result is one a reviewer "
                      + "passed, not proof that every finding in it is right._").AppendLine();

        sb.AppendLine("## Summary").AppendLine();
        if (summaries.Count == 0)
            sb.AppendLine("_No summary has been handed on._").AppendLine();
        foreach (var summary in summaries)
        {
            if (ItemReport.Standing(summary.Record) is { } worth && summary.Record?.Standing != ResultStanding.Unreviewed)
                sb.AppendLine($"> {worth}").AppendLine();
            sb.AppendLine(summary.Text.Trim()).AppendLine();
        }

        sb.AppendLine("## Status").AppendLine();
        var counted = items.GroupBy(i => ItemReport.Status(i.Record)).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}");
        sb.AppendLine($"{items.Count} item(s): {string.Join(", ", counted)}.").AppendLine();
        sb.AppendLine("| Item | Status | What remains |");
        sb.AppendLine("|---|---|---|");
        foreach (var item in items)
            sb.AppendLine($"| {ItemReport.Cell(Name(item))} | {ItemReport.Cell(ItemReport.Status(item.Record))} | {ItemReport.Cell(ItemReport.Remains(item.Record))} |");
        sb.AppendLine();

        sb.AppendLine("## Items").AppendLine();
        foreach (var item in items)
        {
            sb.AppendLine($"### {Name(item)}").AppendLine();
            sb.AppendLine($"Status: {ItemReport.Status(item.Record)}").AppendLine();
            if (item.Record?.Result is { } result)
            {
                if (ItemReport.Standing(item.Record) is { } worth)
                    sb.AppendLine($"> {worth}").AppendLine();
                sb.AppendLine(ItemReport.Values(result)).AppendLine();
            }
            else
                sb.AppendLine("_Result not provided._").AppendLine();
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static string Name(Item item) => item.Names.Count == 0 ? "(no item)" : string.Join(", ", item.Names);

    /// <summary>The summary a step handed on, or null when it handed none.</summary>
    public static string? SummaryOf(StepOutput? output)
    {
        if (output is null) return null;
        using var doc = JsonDocument.Parse(output.ValuesJson);
        return doc.RootElement.TryGetProperty(SummaryField, out var v) && v.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
    }

    /// <summary>
    /// The contract of a step after the items of a report: a summary, as a value, alongside whatever else
    /// it declared - so the model's part of the document is handed on, not written into it.
    /// </summary>
    public static StepOutputSchema WithSummary(StepOutputSchema? declared, int stepNo)
    {
        var summary = new StepOutputField(SummaryField, StepOutputFieldType.Text,
            "The summary for the report: what the findings amount to, in the language of the request. The engine puts it in the report's Summary section; do not edit the report.");
        if (declared is null) return new StepOutputSchema($"step{stepNo}", 1, [summary]);
        return declared.Fields.Any(f => f.Name == SummaryField)
            ? declared
            : declared with { Fields = [.. declared.Fields, summary] };
    }
}
