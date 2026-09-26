namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Workers;
using Enactive.Core.Tools;
using Enactive.Core.Artifacts;

internal sealed class ToolPreflight(IToolRegistry tools, ToolAccess access, Worker worker,
    ReadLedger reads, IArtifactStore store, string root, Guid workspaceId)
{
    internal sealed record Refusal(string Reason, string Reply, string Summary,
        bool DidNotRun = false, string? AsTool = null, bool Answered = false);

    /// <summary>Ordered non-interactive admission: existence, role, repeat, read coverage.
    /// The caller records the refusal and owns subsequent permission/location decisions.</summary>
    internal async Task<Refusal?> CheckAsync(ToolCall call, StepProgress progress, CancellationToken ct)
    {
        if (!tools.Definitions.Any(d => string.Equals(d.Name, call.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var nearest = access.NearestTool(call.Name, worker);
            var reason = $"there is no tool called '{call.Name}'. Nothing ran."
                + (nearest is null
                    ? " Use one of the tools listed for this conversation, spelled exactly as it appears there."
                    : $" The tool is spelled '{nearest}'. Call it again with that name.");
            return new(reason, "ERROR: " + reason, $"{call.Name}: no such tool", true, nearest);
        }
        if (!ToolAccess.Allows(worker, call.Name))
            return new($"not available to the {worker.Role} role",
                $"ERROR: tool '{call.Name}' is not available to the {worker.Role} role.",
                $"{call.Name}: not available to role '{worker.Role}'");
        if (tools.DefinitionOf(call.Name)?.Kind == ToolKind.Command && !HasForce(call)
            && progress.AlreadyRanExactly(call, tools.WorkspaceVersion(workspaceId)))
        {
            var reason = $"'{call.Name}' already ran with these exact arguments earlier in this step, "
                + "with no reported workspace effects or intervening tracked changes. "
                + "Its earlier result is available. If you have a reason to check again "
                + "(state outside the workspace, flakiness you are checking for), send it "
                + "again with \"force\": true.";
            return new(reason, "ERROR: " + reason, $"{call.Name}: refused — an exact repeat", Answered: true);
        }
        var named = ReadLedger.FileNamedBy(call);
        if (reads.Refuse(call, named, tools.DefinitionOf(call.Name),
            named is null || await FileIsThereAsync(root, named, store, ct)) is { } unread)
            return new(unread, "ERROR: " + unread, $"{call.Name}: refused — the file has only been read in part");
        return null;
    }

    /// <summary>Whether a call asks, itself, to be run despite being an exact repeat - see <see cref="ToolArguments.Force"/>.</summary>
    internal static bool HasForce(ToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(ToolArguments.Force, out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool CompleteArguments(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Whether there is a file at this workspace path now - on disk, or as a proposal a staging
    /// store holds. When it cannot be said (a path that does not resolve), true: the guards that ask
    /// then stay as strict as they were, and the tool itself refuses a bad path.
    /// </summary>
    internal static async Task<bool> FileIsThereAsync(string root, string path, IArtifactStore store, CancellationToken ct)
    {
        try
        {
            if (File.Exists(Path.GetFullPath(Path.Combine(root, path))))
                return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return true;
        }

        return await store.TryReadPendingAsync(path, ct) is not null;
    }

}
