namespace Enactive.Agents;

using Enactive.Core.Context;
using System.Text.Json;
using Enactive.Core.Tools;
using Enactive.Core.Artifacts;
using Enactive.Core.Execution;
using static Enactive.Agents.ToolCallParsing;

/// <summary>
/// The tool calls that failed and were never made to work.
///
/// A failed <see cref="ToolResult"/> used to reach only the transcript: the model saw "ERROR:
/// file not found", replied "Done", and the step was recorded as Succeeded because a reply
/// without tool calls was taken as a finished job. A run that never read the file it was asked
/// to read reported green.
///
/// An operation is considered recovered when the SAME call — same tool, same arguments — later
/// succeeds. That is the only recovery this can actually verify; a different call succeeding
/// says nothing about the one that failed. The cost is that an agent which reaches the goal by
/// another route still leaves the step Incomplete, which is the honest reading: what it was
/// asked to do did not happen, whatever else did.
///
/// A lookup that found nothing is held to a different standard. <see cref="ToolResult.IsAnswer"/>
/// marks the failures that ANSWERED — read_file on a path that does not exist, list_dir on a
/// folder that is not there — and guessing at a name and being told no is how anything explores
/// a tree it has not seen. Those are forgiven, with one condition: <b>unless they are all the
/// step has to show for itself.</b> A step whose every action was a lookup that found nothing
/// produced nothing, and "Done" over that is the exact shape this class was built to catch. One
/// call that WORKED is what separates a step exploring from a step with nothing.
/// </summary>
internal sealed class OpenFailures
{
    private readonly IReadOnlyList<ToolDefinition> _definitions;
    public OpenFailures(IReadOnlyList<ToolDefinition> definitions) => _definitions = definitions;
    private bool RepairsFiles(string name) => _definitions.Any(d => d.Name == name && d.RepairsFileFailures);

    private readonly Dictionary<string, string> _byCall = new(StringComparer.Ordinal);

    /// <summary>Lookups that found nothing — see the note above about when these count.</summary>
    private readonly Dictionary<string, string> _foundNothing = new(StringComparer.Ordinal);

    /// <summary>The file each open failure was trying to change, where it named one.</summary>
    private readonly Dictionary<string, string> _fileOf = new(StringComparer.Ordinal);

    /// <summary>
    /// Open failures that named NO file, by the tool that produced them — a call refused for a
    /// missing required argument, which is a sentence that did not parse rather than an action
    /// that did not happen. See <see cref="Succeeded"/>.
    /// </summary>
    private readonly Dictionary<string, string> _namedNothing = new(StringComparer.Ordinal);

    /// <summary>
    /// Calls that NEVER HAPPENED — nothing parsed them, nothing ran them, or a person said no.
    /// Kept apart from the rest because they leave no residue: there is no half-written file
    /// and no broken build behind them, only a sentence that went nowhere.
    /// </summary>
    private readonly Dictionary<string, string> _neverHappened = new(StringComparer.Ordinal);

    private bool _anythingWorked;

    /// <summary>
    /// Whether this step CHANGED anything — a successful call that writes, or one that produced
    /// an artifact. Deliberately stricter than <see cref="_anythingWorked"/>, which a single
    /// read sets: see <see cref="Forgiven"/>.
    /// </summary>
    private bool _anythingChanged;

    /// <summary>
    /// Calls that never happened, in a step that did its work anyway — and which therefore say
    /// nothing about whether the work was done.
    ///
    /// <para><b>Measured 2026-09-22, twice in half an hour.</b> A step verified five wiki
    /// pages, wrote its report and gave its final answer — and was marked Incomplete, with four
    /// dependent steps skipped, because a person had declined to delete a scratch file it did
    /// not need. Half an hour later another step made 114 successful calls over four and a half
    /// minutes, rewrote the same report, gave its final answer — and was failed for one
    /// <c>git</c> call written with a missing pair of quotes, four and a half minutes earlier,
    /// which it never repeated. Both runs did exactly what was asked and both were thrown
    /// away.</para>
    ///
    /// <para><b>Why "changed" and not "worked".</b> A step that sent a malformed write and then
    /// read three files has still not written anything, and forgiving it on the strength of a
    /// read is the exact hole this class exists to close: a step reporting "Done" over an
    /// action that never happened. A step that WROTE something did the thing steps are for, and
    /// a call that never ran is then incidental noise — recorded in the journal, shown to the
    /// reviewer, and not a verdict.</para>
    ///
    /// <para>A call that RAN and failed is never here. A build that broke is evidence, and no
    /// amount of other work makes it not have broken.</para>
    /// </summary>
    private IReadOnlyCollection<string> Forgiven
        => _anythingChanged ? _neverHappened.Keys : Array.Empty<string>();

    public int Count
        => _byCall.Keys.Count(k => !Forgiven.Contains(k))
         + (_anythingWorked ? 0 : _foundNothing.Count);

    /// <summary>True when the step's whole record is lookups that found nothing.</summary>
    public bool NothingButMisses
        => !_anythingWorked && _byCall.Count == 0 && _foundNothing.Count > 0;

    /// <param name="didNotRun">
    /// The call was never attempted. Two things set it, and they are the same thing seen from
    /// two distances: the tool could not READ the call (<see cref="ToolResults.Unreadable"/>),
    /// or the SHELL would not start the line (<see cref="ToolResults.NeverRan"/>). A sentence
    /// that did not parse, in the tool or one layer further down. Such a call is closed by the
    /// same KIND of tool succeeding afterwards, because there is no residue to make good.
    ///
    /// <para>That rule already existed and was written to depend on the tool being one that
    /// writes files, which was never its justification. Reported 2026-08 as
    /// <c>git ["diff HEAD"]</c>: refused before git ran, followed by <c>git ["diff"]</c> and
    /// <c>git ["status"]</c> that worked, a truthful report, a file written — and a run failed
    /// for two calls that never happened.</para>
    /// </param>
    /// <summary>
    /// The operation each open SHELL failure was attempting, where it is one.
    ///
    /// <para>The counterpart of <see cref="_fileOf"/>. A failed write is made good by a later
    /// write to the same FILE, whatever the call looked like; a failed command had no such
    /// notion and could only be made good by re-running the byte-identical string.</para>
    /// </summary>
    private readonly Dictionary<string, string> _shellOf = new(StringComparer.Ordinal);

    /// <summary>
    /// Open failed shell LOOKUPS - dir, findstr, Test-Path - and the paths each asked about that have
    /// not been answered since. See <see cref="ShellLookup"/>: their exit code cannot tell "not there"
    /// from "went wrong", so they are not forgiven; a structured lookup that answers for every one of
    /// those paths settles them.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _lookupOf = new(StringComparer.Ordinal);

    /// <summary>
    /// Open failed EDITS and the state they were after: this file, holding this text, the old text gone.
    /// Closed by that state being confirmed in the file (<see cref="Settle"/>) - not by any later write
    /// to the file, which says nothing about whether the change the edit wanted is in it.
    /// </summary>
    private readonly Dictionary<string, (string Path, string Old, string New)> _editOf = new(StringComparer.Ordinal);

    /// <param name="asTool">
    /// The tool this call was REACHING for, when the name it used was not one. A call to
    /// <c>run-powershell</c> is closed by a <c>run_powershell</c> that works, because that is
    /// the same work done under the name the engine has - while the report still shows what
    /// the model actually typed, so the typo stays visible.
    ///
    /// <para>Without this the entry is filed under a name nothing can ever match, and the step
    /// carries it to the end however thoroughly the model corrected itself.</para>
    /// </param>
    public void Failed(ToolCall call, string? error, bool didNotRun = false, string? asTool = null)
    {
        var key = Key(call);
        _byCall[key] = Line(call, error);

        // Asked FIRST, because it is the strongest thing known about the call: whatever the
        // arguments name, nothing was attempted. Naming the file or the operation would put
        // the call where only doing that same thing again can close it - and there is no
        // "again", because there was never a first time.
        if (didNotRun)
        {
            _namedNothing[key] = Kind(asTool ?? call.Name);
            _neverHappened[key] = Kind(asTool ?? call.Name);
            if (FileNamedBy(call) is { } aimedAt) _neverHappenedAt[key] = aimedAt;
        }
        else if (EditOf(call) is { } edit)
            _editOf[key] = edit;
        else if (FileNamedBy(call) is { } file)
            _fileOf[key] = file;
        else if (ShellOperation.For(call.Name, call.ArgumentsJson) is { } operation)
        {
            _shellOf[key] = operation;
            if (ShellLookup.Paths(call.Name, call.ArgumentsJson) is { Count: > 0 } asked)
                _lookupOf[key] = asked.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        else if (RepairsFiles(call.Name))
            _namedNothing[key] = Kind(call.Name);
    }

    /// <summary>The file a call that never happened was aimed at, where it named one - see <see cref="RefusedByRule"/>.</summary>
    private readonly Dictionary<string, string> _neverHappenedAt = new(StringComparer.Ordinal);

    /// <summary>
    /// The engine refused a call by its own rule (a write boundary): that change will not happen, so a
    /// call aimed at the same file that never happened - one from a turn cut at its limit - is no longer
    /// waiting to be made good. Run dd7ca94b, 2026-09-28: a page step's cut turn held an edit of the
    /// shared report; the step sent it again, the boundary refused it, and the step was failed for the
    /// cut one - a change it had been told it may not make.
    /// </summary>
    public void RefusedByRule(ToolCall call)
    {
        if (FileNamedBy(call) is not { } file) return;
        foreach (var open in _neverHappenedAt.Where(p => string.Equals(ShellLookup.Normal(p.Value), ShellLookup.Normal(file),
                     StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToArray())
            Close(open);
    }

    /// <summary>A lookup whose target is not there. An answer — unless the step has nothing else.</summary>
    public void FoundNothing(ToolCall call, string? error)
    {
        _foundNothing[Key(call)] = Line(call, error);
        // "Not there", said by a tool that can say it, answers a shell lookup's question about it.
        Answered(call);
    }

    /// <summary>The files open edits were changing - the ones whose state <see cref="Settle"/> has to read.</summary>
    public IReadOnlyList<string> EditPaths => _editOf.Values.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>A structured lookup answered - found or not - for the paths it names; see <see cref="_lookupOf"/>.</summary>
    private void Answered(ToolCall call)
    {
        if (ShellTools.IsShell(call.Name) || _definitions.FirstOrDefault(d => d.Name == call.Name)?.Kind != ToolKind.Read)
            return;
        var named = PathsNamedBy(call);
        if (named.Count == 0) return;
        foreach (var (key, asked) in _lookupOf.ToArray())
        {
            asked.RemoveWhere(p => named.Contains(p));
            if (asked.Count == 0) Close(key);
        }
    }

    /// <summary>
    /// Closes the open edits whose wanted state is now in their file: the new text there and the old
    /// text gone (or kept inside the new one). Returns what it closed, to be said.
    /// </summary>
    /// <param name="read">The file's content as the run sees it - staged or on disk - or null when it cannot be read.</param>
    public IReadOnlyList<string> Settle(Func<string, string?> read)
    {
        var settled = new List<string>();
        foreach (var (key, edit) in _editOf.ToArray())
        {
            if (read(edit.Path) is not { } content || !content.Contains(edit.New, StringComparison.Ordinal)
                || (content.Contains(edit.Old, StringComparison.Ordinal) && !edit.New.Contains(edit.Old, StringComparison.Ordinal)))
                continue;
            settled.Add(edit.Path);
            Close(key);
        }
        return settled;
    }

    /// <summary>
    /// A call that worked, and the files it produced.
    ///
    /// <para>Those files close any failure that was trying to change one of them. Reported
    /// 2026-09-07 21:01: an <c>edit_file</c> whose <c>old_string</c> did not match, and a
    /// <c>write_file</c> sent without its <c>path</c> — both on Program.cs, both followed
    /// immediately by a <c>write_file</c> of that same file that WORKED. The tests were written.
    /// The step was marked Incomplete for two calls the model had already made good, plus a
    /// third, and the step after it was skipped.</para>
    ///
    /// <para>The old rule — recovered only when the same call, same arguments, succeeds — took a
    /// tool call for the goal. It is not: the model was never asked to call edit_file with that
    /// exact old_string, it chose to, and when the choice did not work it rewrote the file
    /// instead. The FILE is the thing that was asked for, and the artifact store says which files
    /// a call actually produced, so this is evidence rather than inference.</para>
    /// </summary>
    /// <param name="produced">
    /// <para>A second case, from the same log: <c>write_file {"content":"…"}</c> with the path
    /// left out, refused with <i>'path' is required</i> before it touched anything, and sent
    /// again correctly twenty-two seconds later. That call names no file, so nothing above can
    /// close it — and it is not work that did not happen, it is a sentence that did not parse.
    /// The same tool succeeding afterwards is the model having said it properly.</para>
    ///
    /// <para>The hole that leaves: a model could send a malformed write, never correct it, and
    /// have an unrelated successful write close it. Small, visible in the evidence either way,
    /// and much smaller than the alternative — every mistyped argument poisoning its step for
    /// good, which is what the log showed.</para>
    /// </param>
    public void Succeeded(ToolCall call, IReadOnlyList<ArtifactRef> produced)
    {
        _anythingWorked = true;

        // A file came out of it, or it is the kind of call that writes one. Either is the step
        // having DONE something - which is what lets a call that never happened stop counting.
        if (produced.Count > 0 || RepairsFiles(call.Name))
            _anythingChanged = true;

        Close(Key(call));
        Answered(call);

        foreach (var reference in produced)
            foreach (var open in _fileOf.Where(p => SameFile(p.Value, reference.RelativePath))
                                        .Select(p => p.Key).ToArray())
                Close(open);

        // The same operation, run again and working, makes the earlier attempt good - whatever
        // flags, redirection or pipe it is wearing this time. Measured 2026-09-20: a run wrote
        // 63 passing tests and was reported Incomplete because its first `dotnet test … 2>&1`
        // failed for a missing package, and the verification after the fix was spelled
        // `dotnet test … --nologo 2>&1 | …`. Cause fixed, result proved, different string.
        if (ShellOperation.For(call.Name, call.ArgumentsJson) is { } operation)
            foreach (var open in _shellOf.Where(p => p.Value == operation)
                                         .Select(p => p.Key).ToArray())
                Close(open);

        foreach (var open in _namedNothing.Where(p => p.Value == Kind(call.Name))
                                          .Select(p => p.Key).ToArray())
            Close(open);
    }

    /// <summary>
    /// What a call has to be repeated AS, for a failure that attempted nothing.
    ///
    /// <para>The tool's own name, except that the two shells are one thing. A cmdlet sent to
    /// <c>run_command</c> is refused by cmd.exe having done nothing at all, and the correct
    /// second attempt is the same work sent to <c>run_powershell</c> - the model saying it to
    /// the shell that has the word. Measured 2026-09-20: that is precisely the recovery a run
    /// made, and the step was failed for it, because "the same tool succeeding afterwards"
    /// was read as the same tool NAME.</para>
    ///
    /// <para>Prefixed so it can never be a tool name itself.</para>
    /// </summary>
    private static string Kind(string tool) => ShellTools.IsShell(tool) ? "shell:" : tool;

    private void Close(string key)
    {
        _byCall.Remove(key);
        _neverHappened.Remove(key);
        _foundNothing.Remove(key);
        _fileOf.Remove(key);
        _shellOf.Remove(key);
        _namedNothing.Remove(key);
        _lookupOf.Remove(key);
        _editOf.Remove(key);
        _neverHappenedAt.Remove(key);
    }

    /// <summary>What an edit wanted, from its own arguments: a path, the text to replace and its replacement.</summary>
    private static (string Path, string Old, string New)? EditOf(ToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var root = doc.RootElement;
            string? Text(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return Text("path") is { Length: > 0 } path && Text("old_string") is { Length: > 0 } old && Text("new_string") is { } @new
                ? (path, old, @new) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The paths a structured call names - "path", or each of "paths" - in the form lookups compare.</summary>
    private static HashSet<string> PathsNamedBy(ToolCall call)
    {
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return named;
            if (root.TryGetProperty("path", out var one) && one.ValueKind == JsonValueKind.String && one.GetString() is { Length: > 0 } p)
                named.Add(ShellLookup.Normal(p));
            if (root.TryGetProperty("paths", out var many) && many.ValueKind == JsonValueKind.Array)
                foreach (var item in many.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } q)
                        named.Add(ShellLookup.Normal(q));
        }
        catch (JsonException) { }
        return named;
    }

    /// <summary>The file a call was trying to change, from its own arguments.</summary>
    private static string? FileNamedBy(ToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            // "path" for write/edit/create; "to" is where a move puts the file, which is the
            // one that has to exist afterwards.
            foreach (var name in new[] { "path", "to" })
                if (doc.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text)
                    return text;

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Workspace-relative paths, compared as this platform compares them.</summary>
    private static bool SameFile(string a, string b)
        => string.Equals(a.Replace('\\', '/').Trim('/'), b.Replace('\\', '/').Trim('/'),
                         StringComparison.OrdinalIgnoreCase);

    public string Describe()
    {
        var forgiven = Forgiven;
        var open = _byCall.Where(p => !forgiven.Contains(p.Key)).Select(p => p.Value);

        return string.Join("; ", _anythingWorked ? open : open.Concat(_foundNothing.Values));
    }

    private static string Line(ToolCall call, string? error)
        => $"{call.Name} {Compact(call.ArgumentsJson)} — {error ?? "failed"}";

    private static string Key(ToolCall call) => CallIdentity.Of(call);
}
