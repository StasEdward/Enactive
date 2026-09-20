namespace Enactive.Core.Context;

using System.Text.RegularExpressions;
using Enactive.Core.Tools;

/// <summary>
/// One place a command line appears to write, outside the workspace.
/// </summary>
/// <param name="Token">
/// The token exactly as the model wrote it. Quoted back in the message, because a person reading
/// "this writes outside the workspace" needs to see WHICH part of their command said so.
/// </param>
/// <param name="Path">
/// Where it lands, resolved against the workspace when that is knowable. When it is not - an
/// environment variable, a shell expression - this is the token again and <see cref="Known"/> is
/// false.
/// </param>
/// <param name="Because">What put the token in a writing position: a redirection, a flag, a verb.</param>
public sealed record OutsideWrite(string Token, string Path, string Because, bool Known = true);

/// <summary>
/// Reads a command line and names the places it appears to WRITE outside the workspace.
///
/// <para><b>This is a trap for honest mistakes. It is not a boundary, and it must never be
/// described as one.</b> Everything below is text matching against a string the model wrote, and a
/// command line has arbitrarily many ways to say the same thing. A variable set earlier in the same
/// line, a `cd` before the interesting part, a path assembled at runtime, a build tool reading an
/// output directory out of a config file, a base64 script, a program that simply writes wherever it
/// likes - every one of those walks past this untouched, and NONE of them is exotic. SANDBOX_PLAN
/// section 2 is explicit that text filtering on <c>C:\</c> is worthless against anything
/// deliberate; the adversary this is for is a model that meant well and got the path wrong.</para>
///
/// <para>The reason to build it anyway is that the honest mistake is the common one, and it is
/// currently silent: <c>dotnet publish -o C:\out</c> succeeds today and the person finds out when
/// they notice files somewhere they did not expect. Turning that into a question is worth doing.
/// Pretending it is containment is not, which is why the real guarantee lives in
/// <see cref="WorkspaceGuard"/> - where a path is RESOLVED and the operation refused - and why the
/// shell has no such guarantee at all until an OS-enforced sandbox exists.</para>
///
/// <para><b>False negatives are expected and acceptable; false positives cost a click.</b> That
/// asymmetry is what makes "ask" the right answer rather than "refuse". A refusal on a guess this
/// rough would stop legitimate work, and the model would be told it may not do something it may in
/// fact do - see SANDBOX_PLAN step 4.</para>
/// </summary>
public static class ShellGeography
{
    /// <summary>
    /// Every write this command line appears to make outside <paramref name="workspaceRoot"/> and
    /// outside <paramref name="alsoWritable"/>, in the order they appear. Empty when it sees none -
    /// which is not the same as there being none.
    /// </summary>
    /// <param name="alsoWritable">
    /// Roots the user has already said yes to. Passed in rather than read from anywhere, so this
    /// stays a pure function of its inputs and the store that remembers them can change without
    /// touching the rule.
    /// </param>
    public static IReadOnlyList<OutsideWrite> WritesOutside(
        string? command, string workspaceRoot, IReadOnlyCollection<string>? alsoWritable = null)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(workspaceRoot))
            return Array.Empty<OutsideWrite>();

        var roots = new List<string> { Full(workspaceRoot) };
        foreach (var extra in alsoWritable ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(extra))
                roots.Add(Full(extra));

        var found = new List<OutsideWrite>();
        var seen = new HashSet<string>(WorkspaceGuard.Comparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in Candidates(Tokenize(WithLiteralsResolved(command!))))
        {
            if (Classify(candidate, roots[0], roots) is not { } write)
                continue;

            // One path named twice in one command line is one question, not two.
            if (seen.Add(write.Path))
                found.Add(write);
        }

        return found;
    }

    /// <summary>
    /// Variables the script gives a literal value to, substituted into the rest of it.
    ///
    /// <para><b>Why.</b> The worker is told to put helper scripts in
    /// <c>.enactive/scratch/</c>, and the natural PowerShell way to build one is to name the root
    /// once: <c>$root = ".enactive\scratch\probe"; New-Item -Path "$root\Calc"</c>. Every token
    /// after that is a variable, so every one of them was reported as a write whose place is
    /// unknown - and a question about a path that is plainly inside the workspace was put to the
    /// person on every such command. Measured 2026-09-20: a run did exactly this, the question was
    /// answered conservatively, and the agent was refused the working area it had been instructed
    /// to use.</para>
    ///
    /// <para><b>What it may claim.</b> Nothing new. This does not decide anything - it makes the
    /// same guess with more of the command read. A variable it cannot resolve stays unknown and
    /// still asks; a variable it resolves is then judged like any written-down path, which can
    /// come out inside OR outside. Substituting wrongly - a name reassigned later, a value built
    /// from another variable - can hide a write, and that is the same false negative this class
    /// already documents as expected: the adversary here is a mistaken agent, not a clever one.
    /// </para>
    ///
    /// <para>Only literal assignments, and only from this command. An assignment whose value is
    /// itself an expression teaches nothing and is left alone.</para>
    /// </summary>
    private static string WithLiteralsResolved(string command)
    {
        // $name = "literal"  /  $name='literal'  — PowerShell, which is where this bites.
        var assignments = Regex.Matches(
            command, @"\$(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<q>[""'])(?<value>[^""'$]*)\k<q>",
            RegexOptions.None, TimeSpan.FromSeconds(1));

        if (assignments.Count == 0)
            return command;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in assignments)
            values[m.Groups["name"].Value] = m.Groups["value"].Value;

        // The assignment itself is not a write, and leaving it in would turn the left-hand side
        // into a candidate the moment its name is replaced by a path.
        var text = Regex.Replace(
            command, @"\$[A-Za-z_][A-Za-z0-9_]*\s*=\s*([""'])[^""'$]*\1\s*;?", " ",
            RegexOptions.None, TimeSpan.FromSeconds(1));

        foreach (var (name, value) in values)
            text = Regex.Replace(
                text, @"\$\{?" + Regex.Escape(name) + @"\}?", value.Replace("$", "$$"),
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        return text;
    }

    /// <summary>
    /// The same question asked of a TOOL CALL: reads the command out of the arguments the model
    /// wrote and answers for it. Empty for anything that is not a shell.
    ///
    /// <para>Here rather than at the call site so the engine never has to know which argument name
    /// carries the command for which tool - that is two spellings of one fact, and the second one
    /// is the one that goes stale.</para>
    /// </summary>
    public static IReadOnlyList<OutsideWrite> WritesOutsideFor(
        string tool, string? argumentsJson, string workspaceRoot,
        IReadOnlyCollection<string>? alsoWritable = null)
    {
        if (!ShellTools.IsShell(tool) || string.IsNullOrWhiteSpace(argumentsJson))
            return Array.Empty<OutsideWrite>();

        string? text;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson!);
            text = Read(doc.RootElement, ToolArguments.Command)
                ?? Read(doc.RootElement, ToolArguments.Script);
        }
        catch (System.Text.Json.JsonException)
        {
            // Unreadable arguments are the tool's problem to report, and it reports them better.
            return Array.Empty<OutsideWrite>();
        }

        return WritesOutside(text, workspaceRoot, alsoWritable);

        static string? Read(System.Text.Json.JsonElement root, string name)
            => root.TryGetProperty(name, out var value)
               && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>
    /// What to put in front of a person, or in front of the model when it is refused.
    ///
    /// <para>Same shape as <c>WrongShapeOfArgs</c>, the denied-tool message and the
    /// <c>expectedExitCodes</c> hint: the fix goes in the text somebody is reading at the moment
    /// they are stuck. "Access denied" costs three identical retries; naming the path and the two
    /// ways forward costs none.</para>
    /// </summary>
    public static string Explain(IReadOnlyList<OutsideWrite> writes, string workspaceRoot)
    {
        if (writes.Count == 0)
            return "";

        var lines = writes.Select(w => w.Known
            ? $"  {w.Path}  (from {w.Because})"
            : $"  {w.Token}  (from {w.Because}; a variable, so where it lands is not in the command)");

        return $"This command appears to write outside the workspace ({workspaceRoot}):\n"
             + string.Join("\n", lines)
             + "\n\nWork inside the workspace if you can - a relative path is resolved against it. "
             + "If this path is genuinely where the result belongs, say so and ask for it: it has "
             + "to be a person's decision, not this command's.";
    }

    /// <summary>A token that some part of the command line put in a writing position.</summary>
    private readonly record struct Candidate(string Token, string Because);

    /// <summary>
    /// Splits the line into commands at <c>|</c>, <c>&amp;&amp;</c>, <c>&amp;</c>, <c>;</c> and
    /// looks at each one on its own.
    ///
    /// <para>Per command rather than over the whole line, because position is what most of the
    /// rules below are about: the first token of a command is a verb, and the last argument of a
    /// COPY is its destination. Reading `type a.txt | Out-File C:\b.txt` as one long argument list
    /// loses both facts.</para>
    /// </summary>
    private static IEnumerable<Candidate> Candidates(IReadOnlyList<string> tokens)
    {
        var segment = new List<string>();
        foreach (var token in tokens)
        {
            if (IsSeparator(token))
            {
                foreach (var found in FromOneCommand(segment)) yield return found;
                segment.Clear();
                continue;
            }
            segment.Add(token);
        }

        foreach (var found in FromOneCommand(segment)) yield return found;
    }

    private static IEnumerable<Candidate> FromOneCommand(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            yield break;

        var positional = new List<string>();

        // Known before the flags are walked, because two of the rules below depend on it.
        var verb = Verb(tokens[0]);
        var writes = DeleteOrCreateVerbs.Contains(verb) || CopyVerbs.Contains(verb);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // A redirection is the least ambiguous write there is: `> out.txt`, `2>> log`.
            if (token is ">" or ">>")
            {
                // Always the next token: Tokenize has already cut the arrow off whatever it was
                // written against, so `echo x>out.txt` and `echo x > out.txt` arrive the same way.
                var target = Next(tokens, i + 1);
                if (target.Length > 0)
                {
                    yield return new Candidate(target, "a redirection");
                    i++;
                }
                continue;
            }

            // `-o C:\out`, `--output C:\out`, `-Destination C:\out`.
            if (OutputFlags.Contains(token) || (writes && OutputFlagsWhenWriting.Contains(token)))
            {
                var target = Next(tokens, i + 1);
                if (target.Length > 0)
                {
                    yield return new Candidate(target, $"the {token} option");
                    i++;
                }
                continue;
            }

            // `--output=C:\out`, `-p:PublishDir=C:\out`, `/p:OutputPath=C:\out`.
            if (Glued(token) is { } glued)
            {
                yield return new Candidate(glued.Value, $"the {glued.Name} option");
                continue;
            }

            if (!IsFlag(token))
                positional.Add(token);
        }

        foreach (var found in FromVerb(positional))
            yield return found;
    }

    /// <summary>
    /// The arguments a WRITING verb writes to.
    ///
    /// <para>Which argument depends on the verb, and getting that wrong is a false positive rather
    /// than a miss: <c>copy C:\vendor\lib.dll .</c> READS from outside, which is nobody's mistake,
    /// and asking about it teaches the person to click through the question without reading it.
    /// That is the real cost of a noisy check, so the verbs are separated by where their
    /// destination is rather than lumped together.</para>
    /// </summary>
    private static IEnumerable<Candidate> FromVerb(IReadOnlyList<string> positional)
    {
        if (positional.Count < 2)
            yield break;

        var verb = Verb(positional[0]);
        var args = positional.Skip(1).ToList();

        if (DeleteOrCreateVerbs.Contains(verb))
        {
            foreach (var arg in args)
                yield return new Candidate(arg, $"'{positional[0]}'");
            yield break;
        }

        // Source then destination: only the last one is written.
        if (CopyVerbs.Contains(verb))
            yield return new Candidate(args[^1], $"the destination of '{positional[0]}'");

        // robocopy SOURCE DESTINATION [file ...] - the destination is the second argument, not the
        // last, and the tail is a file mask rather than a path.
        else if (verb == "robocopy" && args.Count >= 2)
            yield return new Candidate(args[1], "the destination of 'robocopy'");
    }

    /// <summary>
    /// Decides whether one candidate token really names somewhere outside, or null when it does
    /// not name a place at all.
    /// </summary>
    private static OutsideWrite? Classify(Candidate candidate, string workspaceRoot, IReadOnlyList<string> roots)
    {
        var token = Unquote(candidate.Token);

        if (token.Length == 0 || IsFlag(token) || Devices.Contains(token.TrimEnd(':')))
            return null;

        // A mask is not a path: `*.txt` as the tail of a copy names files, not a folder.
        if (token.IndexOfAny(new[] { '*', '?' }) >= 0 && !HasSeparator(token))
            return null;

        // A variable, or anything else evaluated by the shell rather than written down. Where this
        // lands cannot be read off the command line - and saying "looks fine" about a path we
        // cannot see is exactly the dishonesty this class is trying not to commit. So it is
        // reported, marked as unknown, and the person is shown the token they wrote.
        if (Expands(token))
            return new OutsideWrite(candidate.Token, token, candidate.Because, Known: false);

        string full;
        try
        {
            full = Path.IsPathRooted(token)
                ? Full(token)
                : Path.GetFullPath(Path.Combine(Full(workspaceRoot), token));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a path this platform can express. Not our question to answer, and not a reason to
            // stop the command: the shell will say so far better than we can.
            return null;
        }

        foreach (var root in roots)
            if (WorkspaceGuard.IsInside(root, full))
                return null;

        return new OutsideWrite(candidate.Token, full, candidate.Because);
    }

    /// <summary>
    /// Splits a command line into tokens, keeping quoted runs together and cutting the shell's own
    /// punctuation off the things it is stuck to.
    ///
    /// <para><c>echo x&gt;C:\out.txt</c> is three tokens, not one, and a check that reads it as one
    /// sees no path at all. Same for <c>2&gt;&amp;1</c>, <c>a|b</c> and <c>x&amp;&amp;y</c>.</para>
    /// </summary>
    private static IReadOnlyList<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        void Flush()
        {
            if (current.Length > 0) tokens.Add(current.ToString());
            current.Clear();
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];

            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c)) { Flush(); continue; }

            if (c is '|' or ';' or '&')
            {
                Flush();
                // && and & are the same separator here; nothing downstream cares which.
                if (c == '&' && i + 1 < command.Length && command[i + 1] == '&') i++;
                tokens.Add(c.ToString());
                continue;
            }

            if (c == '>')
            {
                // A stream number belongs to the arrow, not to the previous word: `2>` and `1>>`.
                var digits = current.Length == 1 && char.IsDigit(current[0]);
                if (digits) current.Clear();
                Flush();

                var arrow = ">";
                if (i + 1 < command.Length && command[i + 1] == '>') { arrow = ">>"; i++; }
                tokens.Add(arrow);
                continue;
            }

            current.Append(c);
        }

        Flush();
        return tokens;
    }

    // ── The tables ───────────────────────────────────────────────────────────
    //
    // Deliberately short. Every entry here is a guess about what somebody meant, and a long table
    // of guesses is not more protection - it is more false positives, each one training the person
    // to click Allow without reading. These are the forms that have actually appeared in runs, plus
    // the obvious neighbours of each.

    private static readonly StringComparer Same = StringComparer.OrdinalIgnoreCase;

    /// <summary>Flags whose value is where the output goes.</summary>
    private static readonly HashSet<string> OutputFlags = new(Same)
    {
        "-o", "--output", "--output-dir", "--out-dir", "--outdir", "-out", "--dest",
        "--destination", "-OutFile", "-Destination", "-FilePath",
        "-ArtifactsPath", "--artifacts-path", "--prefix", "--target-dir",
    };

    /// <summary>
    /// Flags that name a write only when the verb is one that writes.
    ///
    /// <para><c>-Path</c> belongs to <c>Set-Content</c> and to <c>Get-ChildItem</c> alike, and
    /// listing it unconditionally would turn every <c>Get-ChildItem -Path C:\Windows</c> - a read,
    /// and nobody's mistake - into a question. The verb is the only thing that separates them, and
    /// it is sitting at the front of the same command.</para>
    /// </summary>
    private static readonly HashSet<string> OutputFlagsWhenWriting = new(Same)
    {
        "-Path", "-LiteralPath", "-Target",
    };

    /// <summary>Verbs where EVERY path argument is written, created or removed.</summary>
    private static readonly HashSet<string> DeleteOrCreateVerbs = new(Same)
    {
        "del", "erase", "rd", "rmdir", "mkdir", "md", "rm", "touch", "truncate", "unlink",
        "remove-item", "new-item", "set-content", "add-content", "clear-content",
        "out-file", "export-csv", "export-clixml", "tee-object",
    };

    /// <summary>Verbs written SOURCE ... DESTINATION, where only the last argument is a write.</summary>
    private static readonly HashSet<string> CopyVerbs = new(Same)
    {
        "copy", "xcopy", "move", "cp", "mv", "copy-item", "move-item", "rename-item", "ren", "rename",
    };

    /// <summary>Names that are not places. `&gt;nul` is in half the batch files ever written.</summary>
    private static readonly HashSet<string> Devices = new(Same)
    {
        "nul", "con", "prn", "aux", "/dev/null", "/dev/stdout", "/dev/stderr", "$null",
    };

    // ── Small questions about one token ──────────────────────────────────────

    private static bool IsSeparator(string token) => token is "|" or ";" or "&";

    private static string Next(IReadOnlyList<string> tokens, int index)
        => index < tokens.Count && tokens[index] is { } next && next is not (">" or ">>") && !IsSeparator(next)
            ? next
            : "";

    /// <summary>
    /// Whether this token is a switch rather than a path.
    ///
    /// <para><c>/E</c> is a cmd switch and <c>/etc/passwd</c> is a path, and both start with a
    /// slash. A separator INSIDE the token is what tells them apart - which is a heuristic, like
    /// everything else here, and it is wrong for a single-segment absolute path on Unix
    /// (<c>/tmp</c>). That miss is on the safe side of the asymmetry this class is built around.
    /// </para>
    /// </summary>
    private static bool IsFlag(string token)
        => token.StartsWith('-')
        || (token.StartsWith('/') && !token.AsSpan(1).ContainsAny('/', '\\'));

    private static bool HasSeparator(string token) => token.AsSpan().ContainsAny('/', '\\');

    /// <summary>Whether the shell, not the writer, decides what this says.</summary>
    private static bool Expands(string token)
        => token.Contains("$env:", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith('$')
        || token.StartsWith("~/", StringComparison.Ordinal)
        || token.StartsWith("~\\", StringComparison.Ordinal)
        || PercentVariable(token);

    /// <summary>A <c>%NAME%</c> pair, as distinct from a lone percent sign in a file name.</summary>
    private static bool PercentVariable(string token)
    {
        var open = token.IndexOf('%');
        if (open < 0) return false;
        var close = token.IndexOf('%', open + 1);
        if (close <= open + 1) return false;

        for (var i = open + 1; i < close; i++)
            if (!char.IsLetterOrDigit(token[i]) && token[i] != '_' && token[i] != '(' && token[i] != ')')
                return false;

        return true;
    }

    private static string Unquote(string token) => token.Trim('"', '\'');

    private static string Full(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>The verb, without the path it may have been called by or the .exe on the end.</summary>
    private static string Verb(string token)
    {
        var name = Path.GetFileName(Unquote(token));
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4].ToLowerInvariant()
            : name.ToLowerInvariant();
    }

    /// <summary>
    /// A flag with its value stuck on: <c>--output=x</c>, <c>-p:PublishDir=x</c>. MSBuild
    /// properties are the reason this exists — <c>dotnet publish -p:PublishDir=C:\out</c> writes
    /// exactly where <c>-o C:\out</c> does and looks nothing like it.
    /// </summary>
    private static (string Name, string Value)? Glued(string token)
    {
        var equals = token.IndexOf('=');
        if (equals <= 0 || equals == token.Length - 1 || !token.StartsWith('-') && !token.StartsWith('/'))
            return null;

        var name = token[..equals];
        var value = token[(equals + 1)..];

        var known = OutputFlags.Contains(name)
                 || name.EndsWith("OutputPath", StringComparison.OrdinalIgnoreCase)
                 || name.EndsWith("PublishDir", StringComparison.OrdinalIgnoreCase)
                 || name.EndsWith("OutDir", StringComparison.OrdinalIgnoreCase)
                 || name.EndsWith("PackageOutputPath", StringComparison.OrdinalIgnoreCase);

        return known ? (name, value) : null;
    }
}
