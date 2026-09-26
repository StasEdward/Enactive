namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Enactive.Core.Tools;

/// <summary>
/// Reading what a model said it wanted to do, and saying back what it is about to do.
///
/// <para>Lifted out of <see cref="Orchestrator"/> verbatim on 2026-09-08 - the first of the three
/// cuts measured in <c>FIX_PLAN.md</c> §9d. These are the members with no <c>yield</c> and no state,
/// which is the axis that works on that file: its two async iterators cannot be split by NOUN
/// without buffering the event stream or inverting it into a callback, but everything that never
/// yields can simply leave.</para>
///
/// <para>The gain is not only the line count. Every one of these could previously be reached only
/// by running a whole engine. Streamed arguments are preserved verbatim; validation belongs
/// at the execution boundary and must never rewrite a call into a different action.
/// </summary>
internal static class ToolCallParsing
{
    /// <summary>
    /// Best-effort recovery for the "narrated instead of called" failure mode: some local
    /// models, especially quantized ones, sometimes print what a tool call WOULD look like
    /// (a fenced ```json block, or a bare {...} block) instead of emitting a real structured
    /// tool call. If that JSON's keys satisfy exactly one registered tool's required
    /// parameters, treat it as if that tool had actually been called. Deliberately
    /// conservative: any ambiguity (no tool matches, or more than one matches equally well)
    /// returns null rather than guessing.
    /// </summary>
    internal static ToolCall? TryRecoverImplicitToolCall(
        string text, IReadOnlyList<ToolDefinition> tools)
    {
        var candidates = new List<string>();
        foreach (Match m in JsonFenceRegex.Matches(text))
            candidates.Add(m.Groups[1].Value);

        // The old "outermost {...} span" fallback is gone on purpose: it turned any prose containing a
        // brace into a candidate action, which is how a reply reading "Example, do not execute:" wrote a
        // file. Only a fenced ```json block is even considered, and even that is a signal to ASK for a
        // real tool call — never, by itself, permission to run one (see the caller).

        foreach (var candidate in candidates)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(candidate); }
            catch { continue; }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;

                var docKeys = doc.RootElement.EnumerateObject()
                    .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

                ToolDefinition? best = null;
                var bestScore = 0;
                var ambiguous = false;

                foreach (var tool in tools)
                {
                    if (!TryReadSchemaKeys(tool.JsonSchema, out var required, out var properties))
                        continue;
                    if (required.Count == 0 || !required.All(docKeys.Contains))
                        continue; // must at least cover everything this tool requires

                    var score = docKeys.Count(properties.Contains);
                    if (score > bestScore) { best = tool; bestScore = score; ambiguous = false; }
                    else if (score == bestScore && best is not null) { ambiguous = true; }
                }

                if (best is not null && !ambiguous)
                    return new ToolCall(Guid.NewGuid().ToString("N"), best.Name, doc.RootElement.GetRawText());
            }
        }

        return null;
    }

    private static readonly Regex JsonFenceRegex =
        new("```(?:json)?\\s*(\\{[\\s\\S]*?\\})\\s*```", RegexOptions.Compiled);

    internal static bool TryReadSchemaKeys(string jsonSchema, out HashSet<string> required, out HashSet<string> properties)
    {
        required = new HashSet<string>(StringComparer.Ordinal);
        properties = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(jsonSchema);
            var root = doc.RootElement;
            if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                foreach (var p in props.EnumerateObject())
                    properties.Add(p.Name);
            if (root.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                foreach (var r in req.EnumerateArray())
                    if (r.ValueKind == JsonValueKind.String) required.Add(r.GetString()!);
            return true;
        }
        catch { return false; }
    }

    internal static List<ToolCall>? BuildToolCalls(Dictionary<int, ToolCallBuilder> builders)
    {
        if (builders.Count == 0)
            return null;

        return builders
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var b = kv.Value;
                var arguments = b.Arguments.Length > 0 ? b.Arguments.ToString() : "{}";
                return new ToolCall(b.Id ?? Guid.NewGuid().ToString("N"), b.Name ?? "", arguments);
            })
            .ToList();
    }

    /// <summary>
    /// A one-line form for an EVENT LINE. Never for a decision card: shortening what a person is
    /// asked to approve, while running the whole thing, is how a long script gets approved by its
    /// first sentence. See <see cref="DescribeCall"/>.
    /// </summary>
    internal static string Compact(string json)
    {
        var flattened = json.Replace('\n', ' ').Replace('\r', ' ');
        return flattened.Length <= 120 ? flattened : flattened[..120] + "…";
    }

    /// <summary>
    /// The complete action a decision authorises, laid out for a person: each argument in full, on
    /// its own, with newlines intact — a shell script has to be readable as a script. Falls back to
    /// the raw JSON when it does not parse, because showing something odd beats showing nothing.
    /// </summary>
    internal static string DescribeCall(ToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return call.ArgumentsJson;

            var sb = new StringBuilder();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();

                sb.Append(property.Name).AppendLine(":");
                sb.AppendLine(value);
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return call.ArgumentsJson;
        }
    }

    /// <summary>Accumulates a streamed tool call across deltas.</summary>
    internal sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
