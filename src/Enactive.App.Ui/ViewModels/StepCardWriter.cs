namespace Enactive.App.Ui.ViewModels;

using System.Text.Json;

/// <summary>
/// Turns a ToolInvoked summary into what a step card shows. It lives here rather than in the window
/// because two places need it now - the run happening in front of you, and the replay of one that
/// already happened - and a step card that describes the same tool call two different ways
/// depending on which of them you are looking at would be a lie about one of them.
/// </summary>
internal static class StepCardWriter
{
    /// <summary>Adds the tool call to the card as an entry.</summary>
    public static void LogInvocation(StepCardViewModel card, string toolInvokedSummary)
    {
        var (name, args) = Split(toolInvokedSummary);

        switch (name)
        {
            case "write_file":
                card.AddFileOp("Wrote", Hint(args, "path") ?? "a file");
                break;
            case "read_file":
                card.AddFileOp("Read", Hint(args, "path") ?? "a file");
                break;
            case "list_dir":
                card.AddFileOp("Listed", Hint(args, "path") ?? "a directory");
                break;
            case "run_command":
                card.AddCommand(Hint(args, "command") ?? args);
                break;
            default:
                card.AddGenericTool($"Ran {name}");
                break;
        }
    }

    /// <summary>The one-line "what it is doing right now" for the card's header.</summary>
    public static string DescribeActivity(string toolInvokedSummary)
    {
        var (name, args) = Split(toolInvokedSummary);

        return name switch
        {
            "write_file" => Hint(args, "path") is { } p ? $"Writing {p}…" : "Writing a file…",
            "read_file" => Hint(args, "path") is { } p ? $"Reading {p}…" : "Reading a file…",
            "list_dir" => Hint(args, "path") is { } p ? $"Listing {p}…" : "Listing files…",
            "run_command" => Hint(args, "command") is { } c
                ? $"Running: {(c.Length <= 60 ? c : c[..60] + "…")}"
                : "Running a command…",
            _ => $"Running {name}…"
        };
    }

    private static (string Name, string Args) Split(string summary)
    {
        var space = summary.IndexOf(' ');
        return space > 0
            ? (summary[..space], summary[(space + 1)..])
            : (summary, string.Empty);
    }

    private static string? Hint(string argsJson, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        catch
        {
            // Best-effort only: the argument preview is truncated for the log, so it may not parse.
        }
        return null;
    }
}
