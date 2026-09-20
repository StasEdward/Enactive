namespace Enactive.Core.Context;

/// <summary>
/// The shell saying it never ran the line at all.
///
/// <para><b>Why this exists.</b> A failed shell call is held open until it is made good, and every
/// non-zero exit was one kind of thing: a failure. Measured across one day's logs, 2026-09-20, it
/// was two kinds. Twenty-nine calls exited non-zero because a build did not build or a test did
/// not pass — work that was attempted and did not come out. About thirty exited because the shell
/// did not have the word: <c>Select-String</c> nine times, <c>Tee-Object</c> eight,
/// <c>Out-File</c> twice, <c>Select-Object</c>, <c>rm</c>, and once a <c>Tee-File</c> that exists
/// in no shell at all. Every one of those was a model reaching for a PowerShell cmdlet inside
/// <c>run_command</c>, which is cmd.exe. Nothing was executed, nothing was touched, nothing was
/// left half-done — and each one poisoned its step exactly as hard as a broken build.</para>
///
/// <para><b>It is the same distinction <see cref="Tools.ToolResults.Unreadable"/> already makes</b>
/// between "a sentence that did not parse" and "an action that did not happen". Here the sentence
/// did not parse in the shell rather than in the tool, which is a difference in who was reading,
/// not in what happened afterwards: nothing.</para>
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
