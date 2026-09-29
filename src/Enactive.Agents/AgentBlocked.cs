namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Tools;

/// <summary>
/// The step's own word that it cannot go on (Phase 7.2): what stops it, and what would let it go on. ADVISORY -
/// the engine finds the blocks it can see itself (a refused permission, an input that is not there, a blocked step
/// it waits on) whether or not the step says anything, and a report is recorded as the step's word, never promoted
/// to a finding. It ends the step as blocked: not done, not failed, and done again when the run is resumed.
/// </summary>
internal static class AgentBlocked
{
    internal const string ToolName = "report_blocked";

    internal static ToolDefinition Tool { get; } = new(ToolName,
        "Say that this step CANNOT go on, and why - only for something outside the step that it cannot do or get: "
        + "a permission it was refused, a file or input that does not exist, information or a decision only the person has. "
        + "Not for a hard task, and not instead of doing what can be done. It ends the step as BLOCKED - not done; the run "
        + "stops there until a person removes the cause, and then this step is done again.",
        """
        {"type":"object","properties":{
          "reason":{"type":"string","description":"What stops the step, as a fact: what is missing or refused, and where."},
          "needs":{"type":"string","description":"What would let it go on: the permission, the file, the answer."}},
         "required":["reason"],"additionalProperties":false}
        """,
        WorkspaceEffect: WorkspaceEffect.None, Kind: ToolKind.Unknown);

    /// <summary>The report, or why it cannot be taken as one.</summary>
    internal static (string? Reason, string? Needs, string? Refusal) Read(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            string? Text(string name) => doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
            return Text("reason") is { } reason
                ? (reason, Text("needs"), null)
                : (null, null, "a report of being blocked needs its reason: what stops the step.");
        }
        catch (JsonException) { return (null, null, "the arguments are not JSON; send {\"reason\":\"...\",\"needs\":\"...\"}."); }
    }

    /// <summary>The step's report as one line: its word, and what it says it needs.</summary>
    internal static string Line(string reason, string? needs)
        => $"the step reports it cannot go on: {reason}" + (needs is null ? "" : $" (needs: {needs})");
}
