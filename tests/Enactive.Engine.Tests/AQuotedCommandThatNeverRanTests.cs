namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Xunit;

/// <summary>
/// A command named in quotes - a path with a space in it - that the shell does not have never ran,
/// the same as an unquoted one.
///
/// <para><b>Measured 2026-09-24 21:29, run 62f721.</b> A model wrote
/// <c>.enactive/scratch/fix_path.ps1</c>, called it as <c>&amp; '.enactive/scratch/fix_ path.ps1'</c>,
/// and PowerShell said the term is not recognized. The command's head was read by splitting on
/// whitespace - ".enactive/scratch/fix_" - so it did not match the name PowerShell refused, and the
/// call was counted as work that failed. The model ran the right name a second later and fixed the
/// file; the step was left Incomplete anyway, and the nine steps after it were skipped. Paths with
/// spaces are ordinary on Windows, so this was never about one typo.</para>
/// </summary>
public sealed class AQuotedCommandThatNeverRanTests
{
    /// <summary>PowerShell's refusal, verbatim from the run.</summary>
    private const string PowershellRefusal =
        "[stderr]\n"
        + "& : The term '.enactive/scratch/fix_ path.ps1' is not recognized as the name of a cmdlet, function, script file, or \n"
        + "operable program. Check the spelling of the name, or if a path was included, verify that the path is correct and try \n"
        + "again.\n"
        + "At line:2 char:3\n"
        + "+ & '.enactive/scratch/fix_ path.ps1'\n"
        + "+   ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~\n"
        + "    + CategoryInfo          : ObjectNotFound: (.enactive/scratch/fix_ path.ps1:String) [], CommandNotFoundException\n"
        + "    + FullyQualifiedErrorId : CommandNotFoundException";

    [Fact]
    public void A_quoted_script_path_powershell_does_not_have_never_ran()
        => Assert.Equal(ShellVerdict.NeverRan,
                        ShellOutcome.Of("& '.enactive/scratch/fix_ path.ps1'", PowershellRefusal));

    /// <summary>cmd.exe, verbatim: it keeps the path's own double quotes inside its single ones.</summary>
    [Fact]
    public void A_quoted_program_path_cmd_does_not_have_never_ran()
        => Assert.Equal(ShellVerdict.NeverRan,
                        ShellOutcome.Of(
                            "\"C:\\Program Files\\nothere\\x.exe\" --version",
                            "'\"C:\\Program Files\\nothere\\x.exe\"' is not recognized as an internal or external command,\n"
                            + "operable program or batch file."));

    /// <summary>
    /// THE BOUNDARY. The refusal must still be about a command on OUR line: a quoted script that
    /// RAN, and inside it called something that does not exist, failed - it did not "never run".
    /// </summary>
    [Fact]
    public void A_quoted_script_that_ran_and_hit_an_unknown_word_inside_it_failed()
        => Assert.Equal(ShellVerdict.Ran,
                        ShellOutcome.Of(
                            "& '.enactive/scratch/fix path.ps1'",
                            "[stderr]\nTee-File : The term 'Tee-File' is not recognized as the name of a cmdlet, function, "
                            + "script file, or operable program."));
}
