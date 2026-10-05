namespace Enactive.Agents;

using System.Text;
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
/// <para><b>What it does.</b> The MCP tools are never listed one by one. They are named in a catalog -
/// one line each, no schema - carried by one tool, <see cref="LoadToolName"/>, which the model calls with
/// the names it needs; what it loads is callable from its next turn with its full schema, appended at the
/// END of the list so nothing before it moves. Past <see cref="CollapseThreshold"/> tools the catalog is
/// one line per server and <see cref="FindToolName"/> searches it by what is needed. The engine's own tools
/// are always listed in full. Nothing is taken away - a loaded tool goes through the same gates as ever.</para>
///
/// <para><b>Why always, and not past a share of the window.</b> That was the first cure, and it measured the
/// definitions against the window the provider states. A provider that states none - an OpenAI-compatible
/// endpoint with the field left empty, which is how a local model server is usually set up - was never
/// measured, and so was never spared: every schema of every connected server went out with every turn to
/// the models with the least room for them.</para>
/// </summary>
internal sealed class ToolBudget
{
    public const string LoadToolName = "load_tools";
    public const string FindToolName = "find_tools";

    /// <summary>Past this many tools the catalog names servers, not tools, and a search is offered beside it.</summary>
    public const int CollapseThreshold = 40;

    /// <summary>How many tools one step may load, unless the host says otherwise.</summary>
    public const int DefaultMaxLoaded = 8;

    private const int FoundPerSearch = 5;
    private const int Exact = 1000;
    private const int SummaryChars = 80;

    private readonly IReadOnlyList<ToolDefinition> _deferred;
    private readonly int _maxLoaded;
    private readonly List<ToolDefinition> _loaded = [];

    private ToolBudget(IReadOnlyList<ToolDefinition> deferred, int maxLoaded)
    {
        _deferred = deferred;
        _maxLoaded = maxLoaded;
    }

    /// <summary>A tool definition's size in characters, as sent.</summary>
    public static int Size(ToolDefinition d) => d.Name.Length + d.Description.Length + d.JsonSchema.Length + 16;

    /// <summary>Tokens, at the engine's deliberately pessimistic fixed rate: this is decided before any count exists.</summary>
    public static int Tokens(IEnumerable<ToolDefinition> tools) => new TokenScale().TokensFor(tools.Sum(Size));

    /// <summary>
    /// What to list and what to offer on request. The MCP tools go on request whenever there are any; with
    /// none there is nothing to load and nothing changes.
    /// </summary>
    /// <param name="maxLoaded">How many tools this step may load in all.</param>
    /// <param name="loaded">What an earlier attempt at the same step loaded: the conversation it carries on
    /// from already uses them, so they are listed again from the first request, in the order they were loaded.</param>
    public static (ToolDefinition[] Listed, ToolBudget? OnRequest) Split(
        ToolDefinition[] tools, int maxLoaded = DefaultMaxLoaded, IEnumerable<string>? loaded = null)
    {
        var mcp = tools.Where(t => t.Name.StartsWith(McpReach.Prefix, StringComparison.Ordinal)).ToArray();
        if (mcp.Length == 0) return (tools, null);

        var budget = new ToolBudget(mcp, Math.Max(1, maxLoaded));
        foreach (var name in loaded ?? [])
            if (mcp.FirstOrDefault(t => t.Name == name) is { } again && !budget._loaded.Contains(again))
                budget._loaded.Add(again);

        // The engine's own tools in the order they came, then the way to the rest, then what is loaded - last,
        // so that loading a tool adds to the end of the list and moves nothing a provider has already cached.
        return ([.. tools.Where(t => !mcp.Contains(t)), .. budget.Tools(), .. budget._loaded], budget);
    }

    /// <summary>How many tools are offered on request.</summary>
    public int Count => _deferred.Count;

    /// <summary>The servers they come from.</summary>
    public IReadOnlyList<string> Servers => _deferred.Select(d => Server(d.Name)).Distinct().ToArray();

    /// <summary>The names in the catalog, loaded or not, in catalog order.</summary>
    public IReadOnlyList<string> Catalog => _deferred.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>The definitions still waiting in the catalog: named to the model, not shown to it.</summary>
    public IReadOnlyList<ToolDefinition> Waiting => _deferred.Where(d => !_loaded.Contains(d)).ToArray();

    /// <summary>What this step has loaded, in the order it loaded them.</summary>
    public IReadOnlyList<string> Loaded => _loaded.Select(d => d.Name).ToArray();

    /// <summary>
    /// Whether a tool is in the catalog and has not been loaded: the model was never shown its definition, so a
    /// call to it is not one the model can have meant as written. See the caller for what that used to allow.
    /// </summary>
    public bool IsWaiting(string name)
        => _deferred.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
           && !_loaded.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool Collapsed => _deferred.Count > CollapseThreshold;

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

    /// <summary>
    /// What a tool is for, in one line: the first sentence of its description, cut to a line's length. A tool
    /// that describes itself in nothing is known by its name alone.
    /// </summary>
    internal static string Summary(ToolDefinition tool)
    {
        var text = tool.Description.Trim();
        if (text.Length == 0) return "";
        var line = text.IndexOfAny(['\r', '\n']);
        if (line >= 0) text = text[..line].TrimEnd();
        var stop = text.IndexOf(". ", StringComparison.Ordinal);
        if (stop >= 0) text = text[..(stop + 1)];
        return text.Length <= SummaryChars ? text : text[..SummaryChars].TrimEnd() + "...";
    }

    private IEnumerable<ToolDefinition> Tools()
    {
        yield return LoadTool();
        if (Collapsed) yield return FindTool();
    }

    private ToolDefinition LoadTool()
    {
        var text = new StringBuilder();
        text.Append(_deferred.Count).Append(" more tool(s) are available and not listed with the others, to keep each request short. ")
            .Append("Call this with the names of the ones the work needs: each is then callable from your next turn, with its full ")
            .Append("description. Load a tool before calling it; a tool that is not loaded does not run. At most ")
            .Append(_maxLoaded).Append(" in one step.\n");
        if (Collapsed)
        {
            text.Append("By server: ")
                .Append(string.Join("; ", _deferred.GroupBy(d => Server(d.Name)).Select(g => $"{g.Key} ({g.Count()} tools)")))
                .Append(". Find a tool's name with ").Append(FindToolName).Append(", by what it should do.");
        }
        else
        {
            text.Append("Available:\n");
            foreach (var tool in _deferred.OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                var summary = Summary(tool);
                text.Append(tool.Name).Append(summary.Length == 0 ? "" : " - " + summary).Append('\n');
            }
        }
        return new ToolDefinition(LoadToolName, text.ToString().TrimEnd(),
            """{"type":"object","properties":{"names":{"type":"array","items":{"type":"string"},"description":"the exact names of the tools to load"}},"required":["names"],"additionalProperties":false}""",
            WorkspaceEffect: WorkspaceEffect.None, Kind: ToolKind.Read);
    }

    private ToolDefinition FindTool()
    {
        var catalogue = string.Join("; ", _deferred.GroupBy(d => Server(d.Name))
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(d => Short(d.Name)))}"));
        return new ToolDefinition(FindToolName,
            $"Search the {_deferred.Count} tools that are available but not listed. By server - {catalogue}. "
            + "Search them by what you need (a name above, or words such as 'references' or 'diagnostics'); the tools found "
            + "are callable from your next turn, with their full description.",
            """{"type":"object","properties":{"query":{"type":"string","description":"what the tool should do, or its name"}},"required":["query"],"additionalProperties":false}""",
            WorkspaceEffect: WorkspaceEffect.None, Kind: ToolKind.Read);
    }

    /// <summary>
    /// Loads the tools named, and says what became of each name. A name is taken when it is in this step's
    /// catalog, is not loaded yet, and the step's limit has room; anything else is said and passed over - the
    /// call never fails as a whole, and a name that is not there is named, not guessed at.
    /// </summary>
    public (IReadOnlyList<ToolDefinition> Accepted, string Reply) Load(string argumentsJson)
    {
        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("names", out var list)
                && list.ValueKind == JsonValueKind.Array)
                names.AddRange(list.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!.Trim())
                    .Where(n => n.Length > 0));
        }
        catch (JsonException) { }
        if (names.Count == 0)
            return ([], "Give the tools to load in 'names' - a list of exact names from the catalog.");

        var accepted = new List<ToolDefinition>();
        var said = new List<string>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var tool = _deferred.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            if (tool is null) said.Add($"'{name}' is not in this step's catalog - not loaded.");
            else if (_loaded.Contains(tool)) said.Add($"'{tool.Name}' is already loaded.");
            else if (_loaded.Count >= _maxLoaded)
                said.Add($"'{tool.Name}' was not loaded: a step loads at most {_maxLoaded} tools, and {_maxLoaded} are loaded.");
            else
            {
                _loaded.Add(tool);
                accepted.Add(tool);
            }
        }

        var reply = new StringBuilder();
        if (accepted.Count > 0)
        {
            // The definition itself, in the conversation: a model that writes its calls as text has no other
            // place to read a schema from.
            reply.Append("Callable from your next turn:\n");
            foreach (var tool in accepted)
                reply.Append(tool.Name).Append(" - ").Append(tool.Description).Append("\nArguments: ").Append(tool.JsonSchema).Append('\n');
        }
        foreach (var line in said) reply.Append(line).Append('\n');
        return (accepted, reply.ToString().TrimEnd());
    }

    /// <summary>
    /// The tools a search finds - those not loaded yet, best matches first - and what to tell the model.
    /// A term matches a word of a tool's name or description; a name matches outright. What it finds is loaded,
    /// and counts against the step's limit as anything loaded does.
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

        var room = _maxLoaded - _loaded.Count;
        if (room <= 0)
            return ([], $"Nothing more can be loaded: a step loads at most {_maxLoaded} tools, and {_maxLoaded} are loaded.");

        var found = (_deferred
            .Where(d => !_loaded.Contains(d))
            .Select(d => (Tool: d, Score: Score(d, query, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToArray() is var ranked && ranked.Length > 0 && ranked[0].Score >= Exact
                // Asked for by name: that tool, and not everything that shares a word with it.
                ? ranked.TakeWhile(x => x.Score >= Exact).Select(x => x.Tool)
                : ranked.Take(FoundPerSearch).Select(x => x.Tool)).Take(room).ToArray();
        _loaded.AddRange(found);

        return found.Length == 0
            ? ([], $"No tool not already loaded matches '{query}'. Available: "
                + string.Join(", ", _deferred.Where(d => !_loaded.Contains(d)).Select(d => Short(d.Name))) + ".")
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
