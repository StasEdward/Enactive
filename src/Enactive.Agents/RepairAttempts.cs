namespace Enactive.Agents;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Enactive.Core.Tools;

/// <summary>Counts distinct known edits followed by another failure of the same check.</summary>
internal sealed class RepairAttempts
{
    private readonly HashSet<string> _edits = new();
    private readonly Dictionary<string, (int Generation, int Failures)> _checks = new();
    private int _generation;
    public int FailedRepairs { get; private set; }
    public string? Error { get; private set; }
    public bool Consulted { get; set; }

    public void Observe(ToolCall call, ToolResult result, bool isCheck)
    {
        using var json = JsonDocument.Parse(call.ArgumentsJson);
        var arguments = JsonSerializer.Serialize(json.RootElement.EnumerateObject()
            .Where(p => p.Name is not (ToolArguments.Force or ToolArguments.ExpectedExitCodes))
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(call.Name + "\n" + arguments)));
        if (result.Success && result.WorkspaceEffect == WorkspaceEffect.Changed && _edits.Add(key))
            _generation++;
        if (!isCheck || result.DidNotRun) return;
        if (result.Success || result.IsAnswer)
        {
            _checks.Remove(key);
            FailedRepairs = 0;
            Error = null;
            return;
        }
        var count = 0;
        if (_checks.TryGetValue(key, out var previous))
            count = previous.Failures + (_generation > previous.Generation ? 1 : 0);
        _checks[key] = (_generation, count);
        FailedRepairs = count;
        Error = Clip(call.Name + " " + call.ArgumentsJson, 1500) + "\n"
            + Clip(result.Error ?? "", 1500) + "\n" + Clip(result.Output ?? "", 4500);
    }

    internal static string Clip(string text, int limit) => text.Length <= limit ? text
        : text[..(limit / 2)] + "\n[excerpt truncated]\n" + text[^(limit / 2)..];
}
