namespace Enactive.Core.Tools;

/// <summary>Explicit task prohibition, independent of tool permissions and approval.</summary>
public enum ForbiddenTaskEffect { FileDeletion }
public sealed record TaskRestriction(ForbiddenTaskEffect Effect, string SourceQuote);

/// <summary>Invocation allowlist, not OS process/network isolation.</summary>
public sealed record TaskActionPolicy(
    IReadOnlyList<string> AllowedTools, IReadOnlyList<string> CommandPrefixes, string SourceQuote, string Reason)
{
    public bool Allows(ToolCall call, ToolDefinition definition)
    {
        if (!AllowedTools.Contains(definition.Name, StringComparer.Ordinal)) return false;
        if (definition.Kind != ToolKind.Command) return true;
        if (definition.CommandPolicy != CommandPolicySyntax.SimpleCommand) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
            return doc.RootElement.TryGetProperty("command", out var field)
                && field.ValueKind == System.Text.Json.JsonValueKind.String && AllowsCommand(field.GetString()!);
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public bool AllowsCommand(string command)
    {
        // No shell chaining/interpolation. Allowed executables can still have arbitrary effects.
        if (command.IndexOfAny(['\r', '\n', '&', '|', ';', (char)96, '$', '>', '<', '%', '!', '^', '(', ')', '\0']) >= 0)
            return false;
        var text = command.Trim();
        return CommandPrefixes.Any(prefix => text.Equals(prefix, StringComparison.Ordinal)
            || text.StartsWith(prefix + " ", StringComparison.Ordinal));
    }

    public bool SameAs(TaskActionPolicy other) => SourceQuote == other.SourceQuote
        && AllowedTools.Order(StringComparer.Ordinal).SequenceEqual(other.AllowedTools.Order(StringComparer.Ordinal))
        && CommandPrefixes.Order(StringComparer.Ordinal).SequenceEqual(other.CommandPrefixes.Order(StringComparer.Ordinal));
}
