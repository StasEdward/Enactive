namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Tools;

/// <summary>
/// The tool definitions are part of every request, and so part of the budget (amendment E).
///
/// <para><b>Why.</b> Run 80c951, 2026-09-28: a role that names <c>mcp__*</c> was given all 66 tools of
/// a Roslyn refactoring server for a task that only read files. Its first request was 38,909 tokens
/// where the same machine's runs that morning began at 5,700 - over half of a 65,536-token window
/// spent on definitions before any work, sent again with every turn.</para>
///
/// <para><b>What changes.</b> When the definitions would take more than <see cref="SharePercent"/> of
/// the window the step works in, the MCP tools are not listed one by one: they are named in one
/// <see cref="FindToolName"/> tool, which the model searches by what it needs, and what it finds is
/// callable from its next turn with its full schema. The engine's own tools are always listed in full.
/// Nothing is taken away - a found tool, or one called by name, goes through the same gates as ever.</para>
/// </summary>
internal sealed class ToolBudget
{
    public const string FindToolName = "find_tools";

    /// <summary>The share of the window the tool definitions may take before MCP tools are offered on request.</summary>
    public const int SharePercent = 20;

    private const int FoundPerSearch = 5;
    private const int Exact = 1000;

    private readonly IReadOnlyList<ToolDefinition> _deferred;
    private readonly HashSet<string> _given = new(StringComparer.Ordinal);

    private ToolBudget(IReadOnlyList<ToolDefinition> deferred) => _deferred = deferred;

    /// <summary>A tool definition's size in characters, as sent.</summary>
    public static int Size(ToolDefinition d) => d.Name.Length + d.Description.Length + d.JsonSchema.Length + 16;

    /// <summary>Tokens, at the engine's deliberately pessimistic fixed rate: this is decided before any count exists.</summary>
    public static int Tokens(IEnumerable<ToolDefinition> tools) => new TokenScale().TokensFor(tools.Sum(Size));

    /// <summary>
    /// What to list and what to offer on request. With no window stated there is nothing to measure
    /// against, and nothing changes.
    /// </summary>
    /// <param name="window">The size the step works at: its working size when one is set, else its window.</param>
    public static (ToolDefinition[] Listed, ToolBudget? OnRequest, int Tokens) Split(ToolDefinition[] tools, int? window)
    {
        var tokens = Tokens(tools);
        if (window is not > 0 || tokens * 100L <= (long)window.Value * SharePercent) return (tools, null, tokens);
        var mcp = tools.Where(t => t.Name.StartsWith(McpReach.Prefix, StringComparison.Ordinal)).ToArray();
        if (mcp.Length == 0) return (tools, null, tokens);
        var budget = new ToolBudget(mcp);
        return ([.. tools.Where(t => !mcp.Contains(t)), budget.Tool()], budget, tokens);
    }

    /// <summary>How many tools are offered on request.</summary>
    public int Count => _deferred.Count;

    /// <summary>The servers they come from.</summary>
    public IReadOnlyList<string> Servers => _deferred.Select(d => Server(d.Name)).Distinct().ToArray();

    private static string Server(string name)
    {
        var rest = name[McpReach.Prefix.Length..];
        var end = rest.IndexOf("__", StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>The name without its server and the registry's disambiguating suffix, for reading.</summary>
    private static string Short(string name)
    {
        var rest = name[McpReach.Prefix.Length..];
        var end = rest.IndexOf("__", StringComparison.Ordinal);
        var bare = end < 0 ? rest : rest[(end + 2)..];
        var tail = bare.LastIndexOf('_');
        return tail > 0 && bare.Length - tail - 1 == 12 && bare[(tail + 1)..].All(char.IsAsciiHexDigitLower) ? bare[..tail] : bare;
    }

    private ToolDefinition Tool()
    {
        var catalogue = string.Join("; ", _deferred.GroupBy(d => Server(d.Name))
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(d => Short(d.Name)))}"));
        return new ToolDefinition(FindToolName,
            $"{_deferred.Count} more tools are available but not listed, to keep each request short. By server - {catalogue}. "
            + "Search them by what you need (a name above, or words such as 'references' or 'diagnostics'); the tools found "
            + "are callable from your next turn, with their full description.",
            """{"type":"object","properties":{"query":{"type":"string","description":"what the tool should do, or its name"}},"required":["query"],"additionalProperties":false}""",
            WorkspaceEffect: WorkspaceEffect.None, Kind: ToolKind.Read);
    }

    /// <summary>
    /// The tools a search finds - those not given yet, best matches first - and what to tell the model.
    /// A term matches a word of a tool's name or description; a name matches outright.
    /// </summary>
    public (IReadOnlyList<ToolDefinition> Found, string Reply) Find(string argumentsJson)
    {
        string query;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            query = doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() ?? "" : "";
        }
        catch (JsonException) { query = ""; }
        var terms = query.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return ([], "Say what the tool should do in 'query' - a name from the list, or words such as 'references'.");

        var found = _deferred
            .Where(d => !_given.Contains(d.Name))
            .Select(d => (Tool: d, Score: Score(d, query, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToArray() is var ranked && ranked.Length > 0 && ranked[0].Score >= Exact
                // Asked for by name: that tool, and not everything that shares a word with it.
                ? ranked.TakeWhile(x => x.Score >= Exact).Select(x => x.Tool).ToArray()
                : ranked.Take(FoundPerSearch).Select(x => x.Tool).ToArray();
        foreach (var tool in found) _given.Add(tool.Name);

        return found.Length == 0
            ? ([], $"No tool not already given matches '{query}'. Available: "
                + string.Join(", ", _deferred.Where(d => !_given.Contains(d.Name)).Select(d => Short(d.Name))) + ".")
            : (found, "Callable from your next turn: " + string.Join("; ", found.Select(d =>
                $"{d.Name} - {(d.Description.Length <= 200 ? d.Description : d.Description[..200] + "...")}")));
    }

    private static readonly char[] Separators = [' ', ',', ';', '.', '_', '-', '/', ':', '\t', '\n'];

    private static int Score(ToolDefinition tool, string query, string[] terms)
    {
        if (Short(tool.Name).Equals(query.Trim(), StringComparison.OrdinalIgnoreCase) || tool.Name == query.Trim()) return Exact;
        var words = (Short(tool.Name) + " " + tool.Description).ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var nameWords = Short(tool.Name).ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return terms.Sum(t => nameWords.Contains(t) ? 3 : words.Contains(t) ? 1 : 0);
    }
}
