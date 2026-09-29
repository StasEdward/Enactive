namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Tools;

/// <summary>
/// What a step may change - checked before the call runs, for every tool that declares the paths it
/// changes (<see cref="ToolDefinition.ChangedPathArguments"/>): writing, editing, moving from and to,
/// copying to, deleting, creating a folder.
///
/// <para><b>Why.</b> Run 4f1d97, 2026-09-28: twelve page steps each edited one shared report - a
/// section, the contents, the counts - and the next step edited it again. Ambiguous old_string
/// matches, broken tables, and rows marked "Reviewed" by the steps themselves before any review. A
/// step for one item hands its result on as values; what is shared is assembled from those.</para>
///
/// <para><b>The rule.</b> Every step: a document the engine writes (<c>report</c> on a step done for
/// each item) is not changed by any step. A step for one item, in addition: it changes its own item -
/// the path, or what is under it when the item is a folder - and files it created itself; nothing
/// that existed before it or that another step wrote. Paths are compared in one form, and a path that
/// runs through a link is refused, so a link cannot lead outside what is allowed.</para>
///
/// <para><b>What it does not cover.</b> A tool that does not declare its paths - a shell, git, an MCP
/// tool of unknown effect - cannot be checked here. The engine does not claim to bound those: in a step
/// whose results the engine assembles into a document they are not offered at all, and elsewhere the
/// step is told that they are not covered.</para>
/// </summary>
internal sealed class WriteBoundary(
    string root,
    IReadOnlyCollection<string> reserved,
    IReadOnlyList<string>? items,
    Func<IReadOnlySet<string>> owned,
    Action<string> own,
    IReadOnlyCollection<string>? deliverables = null,
    bool readOnly = false)
{
    /// <summary>
    /// What a step planned as read-only is told when it tries to change a file. Run 014ad0a9, 2026-09-29: "Analyze
    /// code and existing test coverage" made 53 edits to test files and one to the application's own markup,
    /// fixing tests that had failed before the run, and sixteen minutes later had not begun the analysis.
    /// </summary>
    internal const string ReadOnlyRefusal = "this step was planned as read-only: it looks and reports, and changes no file. "
        + "Hand on what you found; the steps after it make the changes. (.enactive/scratch is still yours for notes and logs.)";

    /// <summary>Whether this step was planned to change nothing.</summary>
    public bool ReadOnly => readOnly;

    private readonly Dictionary<string, List<string>> _creating = new(StringComparer.Ordinal);

    /// <summary>Whether this is the boundary of a step for one item (and not only the reserved documents).</summary>
    public bool ForItem => items is not null;

    public IReadOnlyList<string> Items => items ?? [];

    /// <summary>Why this call may not change what it names, or null when it may.</summary>
    /// <param name="staged">Whether a path has a write of this step's that is not on disk yet.</param>
    public string? Refuse(ToolCall call, ToolDefinition? definition, Func<string, bool> staged)
    {
        if (definition?.ChangedPathArguments is not { Count: > 0 } arguments) return null;
        var creating = new List<string>();
        foreach (var path in PathsOf(call, arguments))
        {
            var rel = ShellLookup.Normal(path);
            if (reserved.Contains(rel, StringComparer.OrdinalIgnoreCase))
                return $"'{rel}' is written by the engine from the steps' recorded results, and no step changes it. "
                       + $"Hand what you found on with {StepOutputContract.ToolName}.";
            if (readOnly && !rel.StartsWith(WorkspaceGuard.ScratchPrefix + "/", StringComparison.OrdinalIgnoreCase))
                return $"'{rel}' was not changed: " + ReadOnlyRefusal;
            if (items is null) continue;

            string full;
            try { full = WorkspaceGuard.ResolveInside(root, rel); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { continue; }   // the ordinary gate says why

            if (ThroughLink(full))
                return $"'{rel}' goes through a link, and a step for one item does not change what a link may lead to.";
            // What the run's criteria are about is the run's result, not one item's: a step for one item
            // does not create it either (run dd7ca94b: the first page step created the shared report,
            // and so owned it).
            if (deliverables?.Contains(rel, StringComparer.OrdinalIgnoreCase) == true && !WithinItem(rel))
                return $"'{rel}' is what the whole run delivers, not one item's result: a step for one item does not create or "
                       + $"change it. Hand what you found on with {StepOutputContract.ToolName}; the step after the items writes it.";
            if (WithinItem(rel) || owned().Contains(rel)) continue;
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                creating.Add(rel);                                  // new: this step's to create, and then to change
                continue;
            }
            if (staged(rel)) continue;                              // written by this step, not yet on disk
            return $"'{rel}' is not this step's to change: this step is for {string.Join(", ", Items)}, and changes "
                   + "that and the files it creates - not what existed before it or another step wrote. Hand what you "
                   + $"found on with {StepOutputContract.ToolName}; the engine assembles what is shared from every item's result.";
        }
        if (creating.Count > 0) _creating[call.Id] = creating;
        return null;
    }

    /// <summary>
    /// For a step for one item: a call that only LOOKS at a document the engine assembles - reads it, searches
    /// it, compares it - answered instead of run, with why. The document is made from every item's result,
    /// may not exist yet, and nothing an item's step needs is in it. Any argument naming it counts, so no
    /// tool has to declare which of its arguments are paths. Null for any other call.
    /// </summary>
    public string? AssembledByTheEngine(ToolCall call, ToolDefinition? definition)
    {
        if (items is null || reserved.Count == 0 || definition is null || definition.WorkspaceEffect != WorkspaceEffect.None)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            foreach (var value in Strings(doc.RootElement))
                if (reserved.Contains(ShellLookup.Normal(value), StringComparer.OrdinalIgnoreCase))
                    return $"'{ShellLookup.Normal(value)}' is the document the engine assembles from every item's recorded result - "
                           + "it may not exist yet, and this step neither reads nor writes it. Nothing in it is needed here: hand "
                           + $"this item's result on with {StepOutputContract.ToolName}, and it goes into the document.";
        }
        catch (JsonException) { }
        return null;

        static IEnumerable<string> Strings(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: yield return e.GetString()!; break;
                case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) foreach (var s in Strings(x)) yield return s; break;
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) foreach (var s in Strings(p.Value)) yield return s; break;
            }
        }
    }

    /// <summary>A call that went through: the files it created are this step's from now on.</summary>
    public void Succeeded(ToolCall call)
    {
        if (!_creating.Remove(call.Id, out var created)) return;
        foreach (var path in created) own(path);
    }

    private bool WithinItem(string rel)
        => Items.Select(ShellLookup.Normal).Any(item =>
            string.Equals(rel, item, StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(item + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the path, or a folder on the way to it inside the workspace, is a link.</summary>
    private bool ThroughLink(string full)
    {
        var top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        for (var at = full; at is not null && at.Length > top.Length; at = Path.GetDirectoryName(at))
        {
            try
            {
                FileSystemInfo info = Directory.Exists(at) ? new DirectoryInfo(at) : new FileInfo(at);
                if (info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        }
        return false;
    }

    private static IEnumerable<string> PathsOf(ToolCall call, IReadOnlyList<string> arguments)
    {
        var found = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return found;
            foreach (var name in arguments)
                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } p)
                    found.Add(p);
        }
        catch (JsonException) { }
        return found;
    }

    /// <summary>A tool this boundary cannot check: it may change files, and does not say which.</summary>
    public static bool Unchecked(ToolDefinition definition)
        => definition.WorkspaceEffect != WorkspaceEffect.None && definition.ChangedPathArguments is not { Count: > 0 };
}
