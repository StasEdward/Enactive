namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Tools;
using static ToolCallParsing;

/// <summary>Engine-measured evidence for handover and repair consultation, independent of model notes.</summary>
internal static class HandoverEvidence
{
    private static string PathKey(string path) => path.Replace('\\', '/').TrimStart('.', '/');
    /// <summary>How much of the diffs of changed files a handover carries, in all.</summary>
    private const int MaxHandoverDiffChars = 3_000;

    /// <summary>How much of the last command's output a handover carries - its END, where results are.</summary>
    private const int HandoverOutputTailChars = 600;

    /// <summary>
    /// What a handover carries that the model did not write: which files differ from how they were
    /// when the step began, and what the last command it ran said. Appended to its note under a
    /// heading that says which of the two to believe.
    ///
    /// <para><b>Measured 2026-09-24 21:47, run bc3200.</b> Checking that tests catch a breakage, a
    /// step made MonitorClient.cs accept HTTP 404, ran the tests - one failed, as intended - and
    /// was handed over at that moment. Its note said "MonitorClient.cs is currently in its correct,
    /// unbroken state ... What's still to do: None". The next conversation found a failing test,
    /// and instead of undoing the breakage it changed the test to expect 404 to succeed. A reviewer
    /// caught it. A note is a model's memory of its work; what it left on disk is a fact the engine
    /// can measure, and the two were not put side by side.</para>
    /// </summary>
    public static async Task<string> CaptureAsync(IToolRegistry tools,
        IWorkspaceChanges? changes, WorkspaceSnapshot? stepStart, IReadOnlyList<ChatMessage> messages,
        IReadOnlyCollection<string> pending, IReadOnlyCollection<string> touched, string root, CancellationToken ct)
    {
        var facts = new StringBuilder();

        // Staged writes are not on disk, so no snapshot of the disk can see them - said first, and
        // apart, so "nothing differs" below is never read as "nothing was written".
        var waiting = pending.Select(PathKey).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (waiting.Length > 0)
            facts.Append("- Proposed and waiting for the user to apply - NOT on disk yet, so not in the comparison ")
                 .Append($"below: {string.Join(", ", waiting)}\n");

        try
        {
            if (changes is not null && stepStart is not null
                && await changes.TakeAsync(ct) is { } now
                && await changes.CompareAsync(stepStart, now, ct) is { } found)
            {
                // What the snapshots did not measure is not "unchanged". A file this step wrote in a
                // folder the engine skips (bin, obj, the engine's own) or one git ignores is named as
                // such, rather than falling silent under "no file differs".
                var measured = await changes.PathsAsync(stepStart, ct) is { } was && await changes.PathsAsync(now, ct) is { } isNow
                    ? was.Concat(isNow).Select(PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null;
                var unmeasured = measured is null
                    ? Array.Empty<string>()
                    : touched.Select(PathKey).Where(p => !measured.Contains(p) && !waiting.Contains(p, StringComparer.OrdinalIgnoreCase))
                             .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

                if (found.Count == 0)
                    facts.Append("- No file the engine measures differs from how it was when this step began.\n");
                else
                {
                    facts.Append($"- Files that differ from how they were when this step began ({found.Count}). ")
                         .Append("A file you changed only to try something - a deliberate breakage, a temporary ")
                         .Append("edit - is still changed until you put it back:\n");

                    // Which of them a file tool of this step wrote. The rest differ all the same, but the
                    // engine cannot say this step changed them - a command may have, or something
                    // outside the run did (run 3fe4f8, 2026-09-28: three files deleted by nobody in
                    // the run, and the step was told it had changed them). Putting back a file this
                    // step never touched would undo somebody else's work.
                    var mine = touched.Select(PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var room = MaxHandoverDiffChars;
                    foreach (var change in found.Take(20))
                    {
                        facts.Append($"  - {change.Path} ({change.Kind.ToString().ToLowerInvariant()})")
                             .Append(mine.Contains(PathKey(change.Path)) ? "" : " - no file tool of this step wrote it: "
                                 + "a command you ran may have, or something outside the run did. Do not put it back "
                                 + "unless you know this step changed it")
                             .Append('\n');
                        if (change.Kind != FileChangeKind.Modified || change.Diff is not { Length: > 0 } diff)
                            continue;

                        if (room <= 0)
                        {
                            facts.Append("    (its diff is not shown: the room for diffs in this note is used up)\n");
                            continue;
                        }

                        var shown = diff.Length <= room ? diff : diff[..room] + $"\n… (diff cut here: {diff.Length} characters in all)";
                        room -= Math.Min(diff.Length, room);
                        foreach (var line in shown.Split('\n'))
                            facts.Append("    ").Append(line).Append('\n');
                    }
                    if (found.Count > 20)
                        facts.Append($"  - and {found.Count - 20} more\n");
                }

                var (notMeasured, removed) = OnDiskOrGone(root, unmeasured);
                if (notMeasured.Length > 0)
                    facts.Append("- Written by you where the engine does not measure (a folder it skips, or a file git ")
                         .Append($"ignores), so NOT compared above: {string.Join(", ", notMeasured)}\n");
                if (removed.Length > 0)
                    facts.Append($"- Written by you and then removed - not on disk now: {string.Join(", ", removed)}\n");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* a fact that cannot be measured is not stated */ }

        if (LastCommand(messages, tools) is { } last)
        {
            var output = last.Result.TrimEnd();
            var firstLine = output.Split('\n', 2)[0].Trim();
            var tail = output.Length <= HandoverOutputTailChars
                ? output
                : "…" + output[^HandoverOutputTailChars..];
            facts.Append($"- The last command you ran: {last.Command} - {firstLine}. Its output ends:\n");
            foreach (var line in tail.Split('\n'))
                facts.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
        }

        return facts.Length == 0
            ? ""
            : "\n\n---\nMEASURED BY THE ENGINE, not written by you - where this and the note above "
              + "disagree, this is what is true:\n" + facts.ToString().TrimEnd();
    }

    /// <summary>The command line a shell call ran - its 'command' or 'script' - or its arguments as sent.</summary>
    private static string CommandText(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            foreach (var name in new[] { "command", "script" })
                if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? argumentsJson;
        }
        catch (JsonException) { }
        return argumentsJson;
    }

    /// <summary>The last shell command in the conversation and what it answered, or null.</summary>
    private static (string Command, string Result)? LastCommand(IReadOnlyList<ChatMessage> messages, IToolRegistry tools)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != ChatRole.Assistant || messages[i].ToolCalls is not { Count: > 0 } calls)
                continue;

            for (var c = calls.Count - 1; c >= 0; c--)
            {
                var call = calls[c];
                if (tools.DefinitionOf(call.Name)?.Kind != ToolKind.Command)
                    continue;

                var answer = messages.Skip(i + 1).FirstOrDefault(
                    m => m.Role == ChatRole.Tool && m.ToolCallId == call.Id);
                if (answer?.Content is { } content)
                    return (Compact(CommandText(call.ArgumentsJson)), content);
            }
        }

        return null;
    }

    /// <summary>
    /// Of written paths no snapshot listed: the ones that are on disk now (so were written where the
    /// snapshots do not look) and the ones that are not (written and then removed - measured by their
    /// absence at both ends). They were one list, "not measured", and a file created and deleted in
    /// the same step was named as written somewhere unmeasured (run 9ecf0e, 2026-09-24 23:08).
    /// </summary>
    internal static (string[] NotMeasured, string[] Removed) OnDiskOrGone(string root, IEnumerable<string> paths)
    {
        var notMeasured = new List<string>();
        var removed = new List<string>();
        foreach (var path in paths)
        {
            bool there;
            try { there = File.Exists(Path.GetFullPath(Path.Combine(root, path))); }
            catch { there = true; }   // cannot say: claim nothing about it having gone
            (there ? notMeasured : removed).Add(path);
        }
        return (notMeasured.ToArray(), removed.ToArray());
    }
}
