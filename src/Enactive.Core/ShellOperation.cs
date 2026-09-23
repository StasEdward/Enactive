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
    /// <summary>
    /// Tools that are handed an ARGUMENT LIST rather than a command line: no shell parses it, so
    /// a pipe inside one of them is data and not plumbing.
    ///
    /// <para><b>Why they needed this at all.</b> Until now <see cref="For"/> answered only for the
    /// two shells, so a failed <c>git</c> or <c>docker</c> call matched none of the three buckets
    /// in <c>OpenFailures</c> — it carries no <c>path</c>, it is not a shell, it changes no file we
    /// track — and landed in <c>_byCall</c> alone, where the ONLY thing that could close it was
    /// re-sending the byte-identical call. A <c>git push</c> rejected as non-fast-forward, followed
    /// by a pull and a push that worked, left the step holding the first one for good. §9ap solved
    /// exactly this for the shells and the same argument applies here.</para>
    /// </summary>
    private static readonly HashSet<string> ArgumentTools =
        new(["git", "docker"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The operation an argument list performs: the tool, its subcommand, and its first real
    /// operand — the same three parts <see cref="Of"/> keeps, for the same reason.
    ///
    /// <para><c>git show HEAD:a.md</c> and <c>git show HEAD:b.md</c> stay different things, so a
    /// successful read of one cannot clear a failed read of the other. <c>git commit -m "x"</c> and
    /// <c>git commit -m "y"</c> are the same operation, because the flag is dropped and the message
    /// is the operand — which is right: the second commit is the first one made good.</para>
    ///
    /// <para>No plumbing is cut. A shell would split on a pipe; nothing splits an argument list,
    /// so a <c>|</c> inside a commit message is part of the message and stays in the key.</para>
    /// </summary>
    private static string? OfArguments(string tool, JsonElement root)
    {
        if (!root.TryGetProperty(ToolArguments.Args, out var value))
            return null;

        var words = new List<string>();

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } one)
                        words.Add(one);
                break;

            // A single string is split the way the tool splits it, so the same call written both
            // ways gives one key. Both shapes reach git; both have to mean the same operation.
            case JsonValueKind.String:
                if (value.GetString() is { } line)
                    words.AddRange(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                break;

            default:
                return null;
        }

        if (words.Count == 0)
            return null;

        var parts = new List<string> { tool.ToLowerInvariant() };

        foreach (var word in words)
        {
            if (parts.Count >= 3)
                break;

            if (word.StartsWith('-') || word.StartsWith('/'))
                continue;

            parts.Add(word.Replace('\\', '/'));
        }

        return string.Join(' ', parts);
    }

    /// <summary>Where a command line stops being one command: a pipe, a redirect, a separator.</summary>
    private static readonly string[] Plumbing = ["|", "&&", "||", ">>", ">", "<", "&", ";"];

    /// <summary>
    /// The operation this call performs, or null when the call is not a shell or says nothing.
    /// Shaped like <see cref="ShellGeography.WritesOutsideFor"/> so a caller holding a tool name
    /// and its arguments does not have to know which argument carries the command.
    /// </summary>
    public static string? For(string tool, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        var argumentList = ArgumentTools.Contains(tool);
        if (!argumentList && !ShellTools.IsShell(tool))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (argumentList)
                return OfArguments(tool, doc.RootElement);

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
    /// The command up to where it stops being one command. What follows a redirection is where
    /// the output goes, not what was run.
    ///
    /// <para><b>The file descriptor belongs to the redirection.</b> Cutting <c>2&gt;&amp;1</c> at
    /// its <c>&gt;</c> leaves a stray <c>2</c> on the end of the command, and that <c>2</c> is
    /// then read as an operand: <c>type probe.txt 2>&amp;1</c> became "type probe.txt 2" and did
    /// not match <c>type probe.txt</c>. The unit tests missed it because every example in them
    /// was <c>dotnet test X …</c>, where the three parts of an identity are already full before
    /// the stray digit is reached — it took driving the engine end to end to show it.</para>
    /// </summary>
    private static string UpToPlumbing(string command)
    {
        var cut = command.Length;
        var redirect = false;

        foreach (var token in Plumbing)
        {
            var at = command.IndexOf(token, StringComparison.Ordinal);
            if (at < 0 || at >= cut)
                continue;

            cut = at;
            redirect = token is ">" or ">>" or "<";
        }

        // Back over the descriptor the redirection was written with, and only there: a trailing
        // number is an operand anywhere else, and `sleep 5` is not `sleep 10`.
        if (redirect)
            while (cut > 0 && char.IsAsciiDigit(command[cut - 1]))
                cut--;

        return command[..cut];
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
