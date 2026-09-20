namespace Enactive.Core.Context;

/// <summary>
/// The shell saying it never ran the line at all.
///
/// <para><b>Why this exists.</b> A failed shell call is held open until it is made good, and every
/// non-zero exit was one kind of thing: a failure. Measured across three days of logs to
/// 2026-09-20, it was two kinds. Some exited because a build did not build or a test did not pass
/// — work that was attempted and did not come out. And 168 exited because the shell did not have
/// the word: <c>Select-String</c> 49 times, <c>Select-Object</c> 41, <c>Get-FileHash</c> 20,
/// <c>Tee-Object</c> 17, <c>Out-File</c> 15, <c>tail</c> 9, <c>Set-Content</c> 7, <c>rm</c> 5,
/// and 5 times a <c>Tee-File</c> that exists in no shell at all. Nearly every one was a model
/// reaching for a PowerShell cmdlet inside <c>run_command</c>, which is cmd.exe — and each
/// poisoned its step exactly as hard as a broken build, while the failure message advised
/// declaring the exit code "expected" for a word that does not exist.</para>
///
/// <para><b>It is the same distinction <see cref="Tools.ToolResults.Unreadable"/> already makes</b>
/// between "a sentence that did not parse" and "an action that did not happen". Here the sentence
/// did not parse in the shell rather than in the tool — a difference in who was reading, not in
/// what was left behind.</para>
///
/// <para><b>Which is not quite "nothing ran".</b> Of the 168 refusals in three days of logs, 27
/// printed something first: <c>dotnet --version | tail -1</c> gets the version out of dotnet and
/// then finds cmd.exe has no <c>tail</c>. So the honest claim is the narrow one — the shell could
/// not read the line, and no part of it is half-finished waiting to be made good. What a stage
/// before the pipe did to the workspace is not hidden by this: the artifact store and
/// <c>TouchedPaths</c> record that, and they are not consulted here.</para>
///
/// <para><b>And the 2026-09-07 boundary is untouched.</b> <i>"Nothing here rescues a build that did
/// not build."</i> A command that RAN and exited 1 still holds its step open; so does one that ran
/// and was killed. The only thing this reclassifies is a line the shell refused to start.</para>
///
/// <para><b>What keeps it honest.</b> The phrase alone is not enough, because a script that ran
/// can print it about something IT tried to call — and then the outer command really did fail. So
/// the name the shell refused must be the head of a command on the line this tool sent. A refusal
/// naming anything else came from inside something that ran, and stays a failure.</para>
///
/// <para>Windows shells only, deliberately: <c>run_command</c> is cmd.exe and <c>run_powershell</c>
/// is PowerShell, and those are the two vocabularies a wrong guess here would be guessing at. A
/// shell whose wording is not listed simply keeps the old answer, which is the safe one.</para>
/// </summary>
public static class ShellRefusal
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

    /// <summary>Where a command line stops being one command — the same plumbing
    /// <see cref="ShellOperation"/> cuts at, plus the line breaks a script is written across.</summary>
    private static readonly char[] Separators = ['|', '&', ';', '\n', '\r'];

    /// <summary>
    /// Whether the shell refused this line without running it.
    ///
    /// <para>False whenever it cannot be sure, including for a null command: the caller then keeps
    /// treating the call as the failure it already thought it was.</para>
    /// </summary>
    /// <param name="command">The command line or script the tool actually sent.</param>
    /// <param name="output">Everything the shell printed, stdout and stderr together.</param>
    public static bool NeverRan(string? command, string? output)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(output))
            return false;

        return WordItDoesNotHave(command!, output!) || TextItCouldNotParse(command!, output!);
    }

    /// <summary>
    /// PowerShell refusing to COMPILE the script, which is the same answer arrived at one step
    /// earlier.
    ///
    /// <para><b>And a stronger guarantee than the rest of this class gives.</b> A script sent as
    /// one text is parsed in full before a single statement of it runs, so a parse error means
    /// literally nothing executed — where "I do not have that word" can still follow a pipeline
    /// stage that already printed.</para>
    ///
    /// <para><b>Measured 2026-09-20.</b> A run inspected a test project for five minutes, built
    /// it clean, and wrote an accurate report. One throwaway script for counting <c>[Fact]</c>
    /// attributes ended <c>} | Format-Table -AutoSize</c> — <i>An empty pipe element is not
    /// allowed</i>. The model rewrote it 1.6 seconds later and got its table. The step was marked
    /// Incomplete for the typo, and the two steps that were to WRITE the tests were skipped
    /// behind it. Six such events in three days of logs, against 270 shell failures; small, and
    /// it cost a whole run.</para>
    ///
    /// <para><b>What keeps it honest</b> is the same thing as above, in the form the error record
    /// offers: PowerShell echoes the source line it choked on, and that line has to be one WE
    /// sent. A parse error echoing anything else came from a script or an
    /// <c>Invoke-Expression</c> that we did run, and that is a failure.</para>
    /// </summary>
    private static bool TextItCouldNotParse(string command, string output)
    {
        var found = false;
        string? echoed = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Contains("ParserError", StringComparison.Ordinal))
            {
                if (echoed is null || !command.Contains(echoed, StringComparison.Ordinal))
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
    /// The line of OUR script that PowerShell prints under <c>At line:N char:C</c>, or null when
    /// this is not that line. The error record's own fields are written the same way — a leading
    /// <c>+</c> — and so is the caret that underlines the offending token, so both are excluded.
    /// </summary>
    private static string? SourceEcho(string line)
    {
        if (!line.StartsWith('+'))
            return null;

        var text = line[1..].Trim();

        if (text.Length < 2
            || text.StartsWith("CategoryInfo", StringComparison.Ordinal)
            || text.StartsWith("FullyQualifiedErrorId", StringComparison.Ordinal)
            || text.All(c => c is '~'))
            return null;

        return text;
    }

    /// <summary>The shell not having the word at all — see the class summary.</summary>
    private static bool WordItDoesNotHave(string command, string output)
    {
        var heads = Heads(command!);
        if (heads.Count == 0)
            return false;

        var found = false;

        foreach (var line in output!.Split('\n'))
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
