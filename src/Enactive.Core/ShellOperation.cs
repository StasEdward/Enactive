namespace Enactive.Core.Context;

using System.Text.Json;
using Enactive.Core.Tools;

/// <summary>
/// What a command line DOES, as distinct from how it was spelled.
///
/// <para><b>Why this exists.</b> A failed tool call is held open until it is "made good", and for
/// a shell command the only way to make one good was to run the byte-identical command again. On
/// 2026-09-20 a run wrote 63 passing tests, found a real defect in the code under test, and was
/// reported <c>Incomplete</c>: its first <c>dotnet test … 2>&amp;1</c> failed because a package
/// was missing, it added the package, and it verified with
/// <c>dotnet test … --nologo 2>&amp;1 | …</c>. Cause fixed, result proved, different string, and
/// the failure stayed open.</para>
///
/// <para><b>It is the same lesson as <see cref="WorkspaceGuard.KeyFor"/>,</b> which exists because
/// the write journal keyed its entries by the string it was handed: <i>"Callers spell one file
/// many ways … and a model spells it differently in two consecutive tool calls as a matter of
/// course."</i> A command is spelled many ways too. Guarding an action and IDENTIFYING one are
/// different jobs; this is the second.</para>
///
/// <para><b>What it deliberately keeps.</b> The program, its subcommand, and its first real
/// operand — so <c>dotnet build ProjectA</c> and <c>dotnet build ProjectB</c> stay different
/// things and a success against one cannot clear a failure against the other. What it drops is
/// plumbing: flags, redirections and everything past a pipe. Those change constantly while the
/// agent works and never change what was being attempted.</para>
///
/// <para><b>It is for CLOSING a failure and nothing else.</b> It is a heuristic over text, with
/// all of <see cref="ShellGeography"/>'s caveats about how easily that is fooled — the difference
/// is what a wrong answer costs. There, a miss lets a write escape; here, a miss either leaves a
/// failure open that should have closed (the old behaviour, and safe) or closes one that should
/// have stayed open, which needs two different commands to agree on program, subcommand AND
/// operand while meaning different things.</para>
/// </summary>
public static class ShellOperation
{
    /// <summary>Where a command line stops being one command: a pipe, a redirect, a separator.</summary>
    private static readonly string[] Plumbing = ["|", "&&", "||", ">>", ">", "<", "&", ";"];

    /// <summary>
    /// The operation this call performs, or null when the call is not a shell or says nothing.
    /// Shaped like <see cref="ShellGeography.WritesOutsideFor"/> so a caller holding a tool name
    /// and its arguments does not have to know which argument carries the command.
    /// </summary>
    public static string? For(string tool, string? argumentsJson)
    {
        if (!ShellTools.IsShell(tool) || string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var text = Text(doc.RootElement, ToolArguments.Command)
                    ?? Text(doc.RootElement, ToolArguments.Script);

            return Of(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The operation a command line performs, or null when there is nothing to name.</summary>
    public static string? Of(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var head = UpToPlumbing(command!);
        var words = head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return null;

        // The program, lower-cased: shells do not care, and neither should this.
        var parts = new List<string> { words[0].ToLowerInvariant() };

        // Then the subcommand and the first operand, skipping flags. Two is enough to tell
        // `dotnet test X` from `dotnet build X` and from `dotnet test Y`, and few enough that
        // the flags an agent adds while it works do not make a new operation each time.
        foreach (var word in words.Skip(1))
        {
            if (parts.Count >= 3)
                break;

            if (word.StartsWith('-') || word.StartsWith('/'))
                continue;

            // Separators are a spelling too - a model writes a/b in one call and a\b in the next,
            // which is the same reason WorkspaceGuard.KeyFor normalises them.
            parts.Add(word.Replace('\\', '/'));
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// The command up to where it stops being one command. <c>2&gt;&amp;1</c> is caught by the
    /// <c>&gt;</c> in it, which is the point: what follows a redirection is where the output goes,
    /// not what was run.
    /// </summary>
    private static string UpToPlumbing(string command)
    {
        var cut = command.Length;

        foreach (var token in Plumbing)
        {
            var at = command.IndexOf(token, StringComparison.Ordinal);
            if (at >= 0 && at < cut)
                cut = at;
        }

        return command[..cut];
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
