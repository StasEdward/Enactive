namespace Enactive.Core.Tools;

/// <summary>Explicit task prohibition, independent of tool permissions and approval.</summary>
public enum ForbiddenTaskEffect { FileDeletion }
public sealed record TaskRestriction(ForbiddenTaskEffect Effect, string SourceQuote);

/// <summary>Invocation allowlist, not OS process/network isolation.</summary>
public sealed record TaskActionPolicy(
    IReadOnlyList<string> AllowedTools, IReadOnlyList<string> CommandPrefixes, string SourceQuote, string Reason)
{
    /// <summary>
    /// Whether every tool this policy names is a command tool - decided where the policy is agreed, against the
    /// tools of the run (the policy itself holds only names).
    ///
    /// <para>Such a list is a statement about COMMANDS, and is read as one: the tools that read and change the
    /// workspace's files stay available. Read as the whole list of what the run may use, it turned "run the tests
    /// with that command and no other" into a run that could not list a folder or open a file - on 2026-10-04 the
    /// review that wrote the list said beside it that reading and editing files were unaffected, and the engine
    /// refused every one of them for a quarter of an hour. A list that names any other tool is the whole list,
    /// as before; so is a policy made without this flag.</para>
    /// </summary>
    public bool CommandsOnly { get; init; }

    /// <summary>Whether the policy lets this tool be used at all (which commands it may run is <see cref="Allows"/>).</summary>
    public bool Names(ToolDefinition definition)
        => AllowedTools.Contains(definition.Name, StringComparer.Ordinal)
           // Only the tools that work on the workspace's files. A tool of unknown kind - mail, an external
           // server - and a command tool that was not named are not what a list of commands leaves open.
           || (CommandsOnly && definition.Kind is ToolKind.Read or ToolKind.Write or ToolKind.Relocate);

    public bool Allows(ToolCall call, ToolDefinition definition)
    {
        if (!Names(definition)) return false;
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
