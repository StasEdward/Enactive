namespace Enactive.Core.Tools;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Positive evidence of built-in deletion operations. This is not shell containment:
/// scripts, aliases and indirect commands may have unknown effects. Never infer absence from false.</summary>
public static class RecordedOperations
{
    public static bool DeletesFiles(ToolCall call, ToolDefinition? definition)
    {
        if (definition?.FileCoverage == FileCoverageBehavior.Delete) return true;
        // Only recognize a direct, simple built-in shell invocation. Do not scan quoted text,
        // comments or arbitrary script bodies for words like "del".
        if (definition?.Kind != ToolKind.Command || call.Name is not ("run_command" or "run_powershell")) return false;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            var field = call.Name == "run_command" ? "command" : "script";
            if (!doc.RootElement.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String) return false;
            var command = value.GetString()!;
            if (command.IndexOfAny(['\r', '\n', '&', '|', ';', '`']) >= 0) return false;
            if (Regex.IsMatch(command, @"\s(?:--help|--version|-h|/\?|-\?)(?:\s|$)",
                RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))) return false;
            if (call.Name == "run_powershell" && Regex.IsMatch(command, @"(?i)\s-WhatIf(?:\s|$|:)",
                RegexOptions.None, TimeSpan.FromMilliseconds(100))) return false;
            var verbs = call.Name == "run_powershell" ? "Remove-Item|rm|del|erase|ri" : "del|erase|rm|unlink";
            return Regex.IsMatch(command, @"^\s*@?(?:" + verbs + @")\s+\S", RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100));
        }
        catch (JsonException) { return false; }
    }
}
