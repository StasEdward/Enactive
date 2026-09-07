namespace Enactive.Tools;

using System.Text;
using System.Text.Json;
using Enactive.Core.Artifacts;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Replaces one exact passage inside an existing file.
///
/// Until this existed the only way to change a file was <c>write_file</c>, which means the model
/// regenerates the whole document for a two-word correction. That is expensive in proportion to the
/// file — but the real cost is accuracy: a 2026-09-06 run had a local model rewrite a guide it had
/// been asked to fix, and the rewrite arrived with Chinese characters inside an identifier and a
/// port number silently changed. Nothing was wrong with the edit the model intended; the damage came
/// from making it retype three thousand words to express it.
///
/// The match must be UNIQUE. Zero matches means the model is editing something it has not read;
/// several matches mean it cannot know which one it meant. Both are refused with a count, so the
/// next attempt can widen the passage rather than guess.
///
/// The write goes through <see cref="IArtifactStore"/> exactly like <c>write_file</c>: journalled,
/// backed up, stageable, and put back when a reviewer rejects the step. An edit tool that touched
/// the disk directly would be a hole straight through all of that.
/// </summary>
public sealed class EditFileTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        Name: "edit_file",
        Description: "Replace one exact passage in an existing file inside the workspace. "
                   + "'old_string' must appear EXACTLY once — include surrounding lines until it is "
                   + "unique. Prefer this over write_file for changing part of a file: it does not "
                   + "make you reproduce the rest, which is where mistakes get introduced.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? path, oldString, newString;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            path = Text(root, "path");
            oldString = Text(root, "old_string");
            newString = Text(root, "new_string");
        }
        catch (JsonException ex)
        {
            return ToolResults.Fail($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(path))
            return ToolResults.Fail("'path' is required.");
        if (string.IsNullOrEmpty(oldString))
            return ToolResults.Fail("'old_string' is required and cannot be empty.");

        // Absent is not the same as "". An empty new_string deletes the passage, which is a real
        // and useful edit; a MISSING one means the arguments are not what the model meant to send.
        if (newString is null)
            return ToolResults.Fail(
                "'new_string' is required. To delete the passage, pass an empty string explicitly.");

        if (string.Equals(oldString, newString, StringComparison.Ordinal))
            return ToolResults.Fail("'old_string' and 'new_string' are identical — nothing to change.");

        try
        {
            var full = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path);

            // The version to edit is the one a reader would see now: a staged proposal if this run
            // has made one, else the file on disk. Editing the disk underneath an outstanding
            // proposal would silently drop it.
            var staged = await ctx.Artifacts.TryReadPendingAsync(path, ct);
            if (staged is null && !File.Exists(full))
                return ToolResults.Fail($"File not found: {path}. Use write_file to create it.");

            var before = staged ?? await File.ReadAllTextAsync(full, ct);

            // Match as written first; only if that finds nothing do we consider that the two sides
            // may simply disagree about line endings. See RetypedForFile.
            var occurrences = Count(before, oldString);
            if (occurrences == 0)
            {
                var retyped = RetypedForFile(before, oldString);
                if (retyped is not null && Count(before, retyped) > 0)
                {
                    oldString = retyped;
                    newString = RetypedForFile(before, newString) ?? newString;
                    occurrences = Count(before, oldString);
                }
            }

            if (occurrences == 0)
                return ToolResults.Fail(
                    $"'old_string' does not appear in {path}. Read the file and copy the passage "
                    + "exactly, including indentation and line breaks.");
            if (occurrences > 1)
                return ToolResults.Fail(
                    $"'old_string' appears {occurrences} times in {path}; it has to identify one "
                    + "place. Include more of the surrounding text until it is unique.");

            var index = before.IndexOf(oldString, StringComparison.Ordinal);
            var after = string.Concat(before.AsSpan(0, index), newString, before.AsSpan(index + oldString.Length));

            var reference = await ctx.Artifacts.CreateAsync(
                path, ArtifactKind.FileSet, path,
                async stream => await stream.WriteAsync(Encoding.UTF8.GetBytes(after), ct),
                ct);

            var delta = Encoding.UTF8.GetByteCount(after) - Encoding.UTF8.GetByteCount(before);
            var line = LineOf(before, index);

            return ToolResults.Ok(
                output: $"Replaced 1 passage in '{path}' at line {line} "
                      + $"({(delta >= 0 ? "+" : "")}{delta} bytes). The rest of the file is unchanged.",
                artifacts: new[] { reference },
                metadata: new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["line"] = line,
                    ["byteDelta"] = delta,
                    ["editedStagedVersion"] = staged is not null
                });
        }
        catch (Exception ex)
        {
            return ToolResults.Fail($"Could not edit '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// The same passage, written with the line endings the FILE uses — or null when that would
    /// change nothing.
    ///
    /// <para>2026-09-07: three edit_file calls in a row were refused with "'old_string' does not
    /// appear in web-site/index.html", and the passage the model sent was character-for-character
    /// the right one. The file had CRLF endings on all 413 lines; the model sent 210 characters
    /// containing five LFs and no CR. It had copied the passage exactly, as far as it could: a
    /// carriage return is invisible in the text read_file returns, and a model cannot reproduce a
    /// character it cannot see. The advice in the refusal — "copy the passage exactly, including
    /// line breaks" — was advice it had already followed and could never satisfy.</para>
    ///
    /// <para>That made the tool unusable on Windows, where CRLF is the normal state of a text file.
    /// It went unnoticed because <c>edit_file</c> was in no worker's tool list, so nothing had ever
    /// called it on a real file: two defects each hiding the other.</para>
    ///
    /// <para>Retyping the REPLACEMENT the same way matters as much as matching. Splicing an LF
    /// passage into a CRLF file leaves it with mixed endings — a diff that shows the whole block
    /// rewritten, and a repository with autocrlf churning on it — for an edit that was supposed to
    /// touch one line.</para>
    /// </summary>
    internal static string? RetypedForFile(string file, string passage)
    {
        if (passage.Length == 0)
            return null;

        var fileHasCrLf = file.Contains("\r\n", StringComparison.Ordinal);
        var passageHasCr = passage.Contains('\r');

        // Normalising first means a passage that is itself mixed comes out consistent, rather than
        // half-converted by a naive replace.
        if (fileHasCrLf && !passageHasCr)
            return passage.Replace("\n", "\r\n", StringComparison.Ordinal);

        if (!fileHasCrLf && passageHasCr)
            return passage.Replace("\r\n", "\n", StringComparison.Ordinal);

        return null;
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    /// <summary>
    /// Counts non-overlapping occurrences. Stops at two: the only questions are "none", "one" and
    /// "more than one", and a pathological pattern in a large file should not be walked to the end
    /// to learn something already decided.
    /// </summary>
    private static int Count(string haystack, string needle)
    {
        var found = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            found++;
            if (found > 1) return found;
            at += needle.Length;
        }
        return found;
    }

    /// <summary>1-based line of a character index — so the result says WHERE, not just that it worked.</summary>
    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "File path relative to the workspace root." },
        "old_string": { "type": "string", "description": "The exact text to replace. Must occur exactly once in the file." },
        "new_string": { "type": "string", "description": "What to put in its place. An empty string deletes the passage." }
      },
      "required": ["path", "old_string", "new_string"]
    }
    """;
}
