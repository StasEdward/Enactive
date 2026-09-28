namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using Enactive.Core.Events;
using Enactive.Core.Tasks;

/// <summary>
/// How the engine states a step's recorded state in words - its status, and what remains. One place,
/// so the account handed to a later step and the document the engine writes cannot disagree; and the
/// words are separate from the codes they come from (OutcomeCause, ResultStanding), so they can be
/// changed or translated without touching what was recorded.
/// </summary>
internal static class ItemReport
{
    /// <summary>The status, from the engine's record - never from anything the step wrote about itself.</summary>
    public static string Status(StepRecord? record) => record is null ? "Not reached" : record.Cause switch
    {
        OutcomeCause.ReviewRejected => "Rejected by review",
        OutcomeCause.ReviewUnprocessable => "Unconfirmed: the review could not be processed",
        OutcomeCause.ReviewUndecided => "Unconfirmed: the reviewer could not decide",
        OutcomeCause.NotReached => "Not reached",
        OutcomeCause.NotExpanded => "Not given steps",
        OutcomeCause.StepFailed => "Failed",
        OutcomeCause.StepIncomplete or OutcomeCause.ItemsUnfinished => "Not finished",
        _ => record.Standing == ResultStanding.Unreviewed ? "Done, not reviewed" : "Confirmed"
    };

    /// <summary>What remains to be done or known about it, or a dash when nothing does.</summary>
    public static string Remains(StepRecord? record)
    {
        if (record is null) return "Not reached";
        var why = record.Reason is { Length: > 0 } reason ? Gist(reason, 160) : null;
        var result = record.Standing == ResultStanding.NotProvided && record.Outcome != StepOutcomeKind.Skipped
            ? "Result not provided" : null;
        return string.Join("; ", new[] { result, record.Cause == OutcomeCause.None && result is null ? null : why }.OfType<string>())
            is { Length: > 0 } said ? said : "—";
    }

    /// <summary>What the result it handed on is worth, when it is less than confirmed.</summary>
    public static string? Standing(StepRecord? record) => record?.Standing switch
    {
        ResultStanding.Provisional => "Provisional: handed on before the step broke off, not a finished result.",
        ResultStanding.Rejected => "Rejected: the review found this result wrong.",
        ResultStanding.Unconfirmed => "Unconfirmed: no review verdict was reached on this result.",
        ResultStanding.Unreviewed => "Not reviewed.",
        _ => null
    };

    /// <summary>A result's values as Markdown: results per item, lists, text, single values.</summary>
    public static string Values(StepOutput output)
    {
        var sb = new StringBuilder();
        using var doc = JsonDocument.Parse(output.ValuesJson);
        foreach (var field in doc.RootElement.EnumerateObject())
        {
            switch (field.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    sb.AppendLine($"**{field.Name}**").AppendLine();
                    foreach (var entry in field.Value.EnumerateObject())
                        sb.AppendLine($"- `{entry.Name}`: {Line(entry.Value)}");
                    sb.AppendLine();
                    break;
                case JsonValueKind.Array:
                    sb.AppendLine($"**{field.Name}**").AppendLine();
                    foreach (var item in field.Value.EnumerateArray())
                        sb.AppendLine($"- {Line(item)}");
                    sb.AppendLine();
                    break;
                case JsonValueKind.String when (field.Value.GetString() ?? "").Contains('\n'):
                    sb.AppendLine($"**{field.Name}**").AppendLine().AppendLine(field.Value.GetString()!.Trim()).AppendLine();
                    break;
                default:
                    sb.AppendLine($"**{field.Name}**: {Line(field.Value)}").AppendLine();
                    break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Line(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()!.Replace("\r", "").Replace("\n", " ")
        : value.GetRawText();

    /// <summary>Text for a table cell: one line, no pipes, bounded.</summary>
    public static string Cell(string text) => Gist(text, 200).Replace("|", "\\|");

    private static string Gist(string text, int max)
    {
        var flat = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
