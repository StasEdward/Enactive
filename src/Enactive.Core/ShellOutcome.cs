namespace Enactive.Core.Context;

/// <summary>What a shell's non-zero exit actually means. See <see cref="ShellOutcome"/>.</summary>
public enum ShellVerdict
{
    /// <summary>The command ran and did not come out. A build that did not build.</summary>
    Ran,

    /// <summary>The shell would not start it: no such word, text it could not parse, a call it
    /// could not bind. A sentence that did not parse, not an action that did not happen.</summary>
    NeverRan,

    /// <summary>It ran, it was a lookup, and the thing it looked for is not there.</summary>
    FoundNothing,
}

/// <summary>
/// Reading a failed shell call for which of three things happened to it.
///
/// <para><b>Why this exists.</b> A failed shell call is held open until it is made good, and every
/// non-zero exit was one kind of thing: a failure. Measured over three days of logs to 2026-09-20,
/// 121 shell failures were at least three kinds — 65 work that was attempted and did not come out,
/// 48 a shell that would not start the line at all, 8 a lookup told there is no such file. All
/// three poisoned their step equally hard, and the failure message advised declaring the exit code
/// "expected" for words that do not exist and files that are not there.</para>
///
/// <para><b>Both of the other two are distinctions the engine already makes elsewhere</b> and had
/// simply never made for a shell. <see cref="Tools.ToolResults.Unreadable"/> separates "a sentence
/// that did not parse" from "an action that did not happen"; <see cref="Tools.ToolResults.NotFound"/>
/// says that <i>"guessing at a path and being told no is how exploring works"</i> — and grants that
/// to <c>read_file</c>, <c>list_dir</c> and <c>search_files</c> while a <c>Select-String</c> asking
/// the identical question was fatal. Which tool asked should not decide whether a run survives.</para>
///
/// <para><b>Reported 2026-09-20 23:16, which is what made this a type instead of a third flag.</b>
/// A run was asked to check the wiki against the source. It produced a 662-line report: 80 claims
/// confirmed, 21 contradicted, 15 drift findings with file and line. It was failed. Two of the
/// three calls that failed it were <c>Select-String -Path src/Enactive.Agents/RunReport.cs</c> and
/// <c>…/Enactive.Remote.Host/RemoteAccessService.cs</c> — paths the WIKI gives, for files that live
/// in <c>Enactive.Core</c> and <c>App.Ui</c>. The misses were the finding. The run was failed for
/// discovering the thing it was asked to discover.</para>
///
/// <para><b>What keeps it honest.</b> Nothing here is decided from prose alone. A word the shell
/// does not have must be the head of a command on the line WE sent; a parse error must echo a line
/// WE sent; a lookup that found nothing must be every error in the output and must come from a
/// cmdlet that only reads. A refusal, a parse error or a missing path belonging to something that
/// RAN keeps the whole call a failure. And the 2026-09-07 line is untouched either way: nothing
/// here rescues a build that did not build.</para>
///
/// <para><b>It is not quite "nothing ran".</b> Of the refusals in three days, 27 printed something
/// first: <c>dotnet --version | tail -1</c> gets the version out of dotnet and only then finds
/// cmd.exe has no <c>tail</c>. So the claim is the narrow one — the shell could not read the line,
/// and no part of it is half-finished waiting to be made good. What a stage before the pipe did to
/// the workspace is recorded by the artifact store and <c>TouchedPaths</c>, which are not consulted
/// here.</para>
///
/// <para>Windows shells only, deliberately: <c>run_command</c> is cmd.exe and <c>run_powershell</c>
/// is PowerShell, and those are the two vocabularies a wrong guess would be guessing at. A shell
/// whose wording is not listed keeps the old answer, which is the safe one.</para>
/// </summary>
public static class ShellOutcome
{
    /// <summary>
    /// How each shell says it does not have the word.
    ///
    /// <para>cmd.exe: <c>'foo' is not recognized as an internal or external command,</c>.
    /// PowerShell: <c>The term 'foo' is not recognized as the name of a cmdlet, function, script
    /// file, or operable program.</c> — the longer form differs across PowerShell versions past
    /// this point, so it is matched no further.</para>
    /// </summary>
    private static readonly string[] Phrases =
    [
        "is not recognized as an internal or external command",
        "is not recognized as the name of a cmdlet",
        "is not recognized as a name of a cmdlet",
    ];

    /// <summary>
    /// PowerShell error ids that mean the call was never bound, so the cmdlet never executed.
    /// Matched as a prefix because the argument-validation family has several spellings.
    /// </summary>
    private static readonly string[] NotBound =
    [
        "NamedParameterNotFound",
        "PositionalParameterNotFound",
        "ParameterArgumentValidationError",
        "MissingArgument",
        "AmbiguousParameter",
        "CannotConvertArgument",
    ];

    /// <summary>
    /// The cmdlets whose "there is no such path" is an ANSWER rather than work that did not happen.
    ///
    /// <para>Deliberately short, and every entry only READS. A <c>Set-Content</c> or a
    /// <c>Move-Item</c> that cannot find its target is an edit that did not happen — which is the
    /// same line <see cref="Tools.ToolResults.NotFound"/> already draws for the file tools, in the
    /// same words.</para>
    /// </summary>
    private static readonly string[] Readers =
    [
        "SelectStringCommand",
        "GetContentCommand",
        "GetChildItemCommand",
        "GetItemCommand",
        "ResolvePathCommand",
    ];

    /// <summary>Where a command line stops being one command — the same plumbing
    /// <see cref="ShellOperation"/> cuts at, plus the line breaks a script is written across.</summary>
    private static readonly char[] Separators = ['|', '&', ';', '\n', '\r'];

    /// <summary>
    /// What happened to a shell call that exited non-zero.
    ///
    /// <para><see cref="ShellVerdict.Ran"/> whenever it cannot be sure, including for a null
    /// command: the caller then keeps treating the call as the failure it already thought it was.
    /// </para>
    /// </summary>
    /// <param name="command">The command line or script the tool actually sent.</param>
    /// <param name="output">Everything the shell printed, stdout and stderr together.</param>
    public static ShellVerdict Of(string? command, string? output)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(output))
            return ShellVerdict.Ran;

        if (WordItDoesNotHave(command!, output!) || CouldNotRead(command!, output!))
            return ShellVerdict.NeverRan;

        return LookedAndFoundNothing(output!) ? ShellVerdict.FoundNothing : ShellVerdict.Ran;
    }

    // ── Never ran ────────────────────────────────────────────────────────────

    /// <summary>The shell not having the word at all — see the class summary.</summary>
    private static bool WordItDoesNotHave(string command, string output)
    {
        var heads = Heads(command);
        if (heads.Count == 0)
            return false;

        var found = false;

        foreach (var line in output.Split('\n'))
        {
            var at = IndexOfPhrase(line);
            if (at < 0)
                continue;

            // Every refusal has to be about a command on OUR line. One that is not came from
            // something that ran, and that is a failure however the others are read.
            if (RefusedName(line, at) is not { } name || !heads.Contains(name))
                return false;

            found = true;
        }

        return found;
    }

    /// <summary>
    /// PowerShell refusing to COMPILE the script, or to BIND a call in it. Two moments, one answer:
    /// the cmdlet never executed.
    ///
    /// <para>A parse error is the stronger of the two — a script sent as one text is parsed in full
    /// before a single statement of it runs, so literally nothing happened. Measured 2026-09-20: a
    /// throwaway script for counting <c>[Fact]</c> attributes ended <c>} | Format-Table -AutoSize</c>
    /// (<i>An empty pipe element is not allowed</i>); the model rewrote it 1.6 seconds later and got
    /// its table, and the two steps that were to WRITE the tests were skipped behind the typo.</para>
    ///
    /// <para>A binding error is the same thing one moment later: <c>Select-String -Path src -Include
    /// *.cs -Recurse</c> — <c>Select-String</c> has no <c>-Recurse</c>, so nothing was searched.</para>
    ///
    /// <para><b>What keeps it honest:</b> PowerShell echoes the source line it choked on, and that
    /// line has to be one WE sent. An error echoing anything else came from a generated script or
    /// an <c>Invoke-Expression</c> that we DID run, and that is a failure.</para>
    /// <para><b>Except when it cannot point at a line at all.</b> Reported 2026-09-22: a step made
    /// 43 successful edits over seven minutes and was failed for one script ending
    /// <c>The string is missing the terminator: '.</c> — <c>ParserError</c>,
    /// <c>TerminatorExpectedAtEndOfString</c>, and NO echo, because a string that never ends leaves
    /// no single offending line to show. The rule above then read "no echo, so not ours" and called
    /// it a failure of the work. It was a quote mark.</para>
    ///
    /// <para>So an unlocatable parse error counts as ours, and what keeps THAT honest is the absence
    /// of a file location: a parse error from a script FILE we ran always says which file and which
    /// line, and one from the text we sent inline does not. Nothing else can have run either way —
    /// a script is compiled in full before its first statement, which is the argument this whole
    /// branch rests on.</para>
    /// </summary>
    private static bool CouldNotRead(string command, string output)
    {
        var found = false;
        string? echoed = null;
        var locatable = Locatable(output);

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Contains("ParserError", StringComparison.Ordinal) || IsNotBound(line))
            {
                var ours = echoed is { } text
                    ? command.Contains(text, StringComparison.Ordinal)
                    // Nothing to compare against - see the note above. A binding error always has a
                    // line to echo, so this reaches only a parse error that has none.
                    : !locatable && line.Contains("ParserError", StringComparison.Ordinal);

                if (!ours)
                    return false;

                found = true;
                continue;
            }

            if (SourceEcho(line) is { } source)
                echoed = source;
        }

        return found;
    }

    /// <summary>
    /// Whether the output says WHERE the error was, in a file. "At C:\tmp\x.ps1:3 char:1" is a
    /// script we ran; "At line:4 char:12" is the text we sent, and so is no location at all.
    /// </summary>
    private static bool Locatable(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith("At ", StringComparison.Ordinal)
                && !line.StartsWith("At line:", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsNotBound(string line)
    {
        var at = line.IndexOf("FullyQualifiedErrorId", StringComparison.Ordinal);
        if (at < 0)
            return false;

        foreach (var id in NotBound)
            if (line.IndexOf(id, at, StringComparison.Ordinal) >= 0)
                return true;

        return false;
    }

    /// <summary>
    /// The line of OUR script that PowerShell prints under <c>At line:N char:C</c>, or null when
    /// this is not that line. The error record's own fields are written the same way — a leading
    /// <c>+</c> — and so is the caret that underlines the offending token, so both are excluded.
    ///
    /// <para>A long line is echoed CUT, with a trailing <c>...</c>. Comparing the whole of that
    /// against the script would never match, and the answer would silently fall back to "it ran" on
    /// exactly the long scripts a model gets wrong most often — so the marker is dropped and the
    /// prefix is what has to be ours.</para>
    /// </summary>
    private static string? SourceEcho(string line)
    {
        if (!line.StartsWith('+'))
            return null;

        var text = line[1..].Trim();

        // Cut on BOTH sides when the offending token sits in the middle of a long line. Only the
        // trailing marker was handled, and the leading one then made every such echo compare as
        // "... TORE|ENACTIVE_LOG_LEVEL' -SimpleMatch -Recurse | ForEa" - a string that is in no
        // command anybody ever sent, so the answer fell back to "it ran". Measured 2026-09-22,
        // 20:45: Select-String given a -Recurse it does not have, nothing searched, and the step
        // failed with two more skipped behind it.
        if (text.StartsWith("...", StringComparison.Ordinal))
            text = text[3..].TrimStart();

        if (text.EndsWith("...", StringComparison.Ordinal))
            text = text[..^3].TrimEnd();

        if (text.Length < 2
            || text.StartsWith("CategoryInfo", StringComparison.Ordinal)
            || text.StartsWith("FullyQualifiedErrorId", StringComparison.Ordinal)
            || text.All(c => c is '~'))
            return null;

        return text;
    }

    // ── Found nothing ────────────────────────────────────────────────────────

    /// <summary>
    /// Every error the call produced is a reading cmdlet saying the path is not there.
    ///
    /// <para>PowerShell states this as structured data rather than prose — <c>FullyQualifiedErrorId
    /// : PathNotFound,Microsoft.PowerShell.Commands.SelectStringCommand</c> names the condition AND
    /// the cmdlet — so both halves of the question are answered without reading a sentence.</para>
    ///
    /// <para><b>Every</b> error, because a script that looked something up AND did something else
    /// that broke is a script that broke. And at least one, because a call with no error record at
    /// all failed for some reason this cannot see.</para>
    /// </summary>
    private static bool LookedAndFoundNothing(string output)
    {
        var any = false;

        foreach (var raw in output.Split('\n'))
        {
            var at = raw.IndexOf("FullyQualifiedErrorId", StringComparison.Ordinal);
            if (at < 0)
                continue;

            var id = raw[at..];

            if (id.IndexOf("PathNotFound", StringComparison.Ordinal) < 0)
                return false;

            var reader = false;
            foreach (var name in Readers)
                if (id.IndexOf(name, StringComparison.Ordinal) >= 0)
                    reader = true;

            if (!reader)
                return false;

            any = true;
        }

        return any;
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>The program each command on the line starts with, lower-cased.</summary>
    private static HashSet<string> Heads(string command)
    {
        var heads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in command.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var words = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0)
                heads.Add(words[0].Trim('"', '\'', '(', '`'));
        }

        return heads;
    }

    private static int IndexOfPhrase(string line)
    {
        foreach (var phrase in Phrases)
        {
            var at = line.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
                return at;
        }

        return -1;
    }

    /// <summary>
    /// The name in quotes that the refusal is about. Both shells put it immediately before the
    /// phrase — cmd at the start of the line, PowerShell after <c>The term</c> — so the last
    /// quoted run before the phrase is the one.
    /// </summary>
    private static string? RefusedName(string line, int phraseAt)
    {
        var head = line[..phraseAt];

        var close = head.LastIndexOf('\'');
        if (close <= 0)
            return null;

        var open = head.LastIndexOf('\'', close - 1);
        if (open < 0 || close - open <= 1)
            return null;

        return head[(open + 1)..close];
    }
}
