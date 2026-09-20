namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Xunit;

/// <summary>
/// A command the shell would not start did not fail — it did not happen.
///
/// <para>Measured over one day of logs, 2026-09-20: about thirty non-zero exits were a PowerShell
/// cmdlet sent to <c>run_command</c>, which is cmd.exe. <c>Select-String</c> nine times,
/// <c>Tee-Object</c> eight, <c>Out-File</c> twice, and once a <c>Tee-File</c> that exists in no
/// shell at all. Each held its step open exactly as hard as the twenty-nine builds that really
/// did fail, and the model was then advised to declare the exit code "expected" — for a word that
/// does not exist.</para>
///
/// <para>What these tests are really guarding is the OTHER side: the line from 2026-09-07 that
/// <i>"nothing here rescues a build that did not build"</i>. Every test below that ends in
/// <c>False</c> is that line.</para>
/// </summary>
public sealed class ShellRefusalTests
{
    /// <summary>cmd.exe, verbatim, on the commonest of the measured cases.</summary>
    private const string CmdRefusal =
        "'Select-String' is not recognized as an internal or external command,\n"
        + "operable program or batch file.";

    [Fact]
    public void Cmd_refusing_a_cmdlet_never_ran()
        => Assert.True(ShellRefusal.NeverRan("Select-String -Path log.txt -Pattern error", CmdRefusal));

    /// <summary>PowerShell's error record, which names the term a second time and in a different place.</summary>
    [Fact]
    public void Powershell_refusing_an_unknown_word_never_ran()
    {
        const string output =
            "Tee-File : The term 'Tee-File' is not recognized as the name of a cmdlet, function, "
            + "script file, or operable program. Check the spelling of the name, or if a path was "
            + "included, verify that the path is correct and try again.\n"
            + "At line:1 char:1\n"
            + "+ Tee-File out.txt\n"
            + "+ ~~~~~~~~\n"
            + "    + CategoryInfo          : ObjectNotFound: (Tee-File:String) [], CommandNotFoundException\n"
            + "    + FullyQualifiedErrorId : CommandNotFoundException";

        Assert.True(ShellRefusal.NeverRan("Tee-File out.txt", output));
    }

    /// <summary>The refused word is not always the first on the line — a pipe has heads of its own.</summary>
    [Fact]
    public void A_cmdlet_after_a_pipe_is_still_a_head_of_this_line()
        => Assert.True(ShellRefusal.NeverRan(
            "dotnet test | Tee-Object -FilePath out.txt",
            "'Tee-Object' is not recognized as an internal or external command,"));

    /// <summary>
    /// THE BOUNDARY. A build that ran and printed that phrase about something IT called did run,
    /// and failed. The name refused is not on the line this tool sent, and that is the whole
    /// difference between "the shell has no such word" and "the thing I started fell over".
    /// </summary>
    [Fact]
    public void A_build_whose_own_script_hit_an_unknown_tool_really_failed()
    {
        const string output =
            "Determining projects to restore...\n"
            + "  Restored C:\\ws\\App\\App.csproj (in 412 ms).\n"
            + "EXEC : error : 'protoc' is not recognized as an internal or external command,\n"
            + "C:\\ws\\App\\App.csproj(31,5): error MSB3073: the command exited with code 9009.\n"
            + "Build FAILED.";

        Assert.False(ShellRefusal.NeverRan("dotnet build App/App.csproj", output));
    }

    /// <summary>
    /// And one refusal that belongs to us does not launder another that does not. A line may
    /// genuinely start and then have something inside it go missing.
    /// </summary>
    [Fact]
    public void One_refusal_from_inside_is_enough_to_keep_it_a_failure()
        => Assert.False(ShellRefusal.NeverRan(
            "build.cmd | Select-String error",
            "'protoc' is not recognized as an internal or external command,\n"
            + "'Select-String' is not recognized as an internal or external command,"));

    /// <summary>A build that did not build. Nothing about this reads as a refusal.</summary>
    [Fact]
    public void A_compile_error_is_a_real_failure()
        => Assert.False(ShellRefusal.NeverRan(
            "dotnet build",
            "MoneyTests.cs(1,7): error CS0246: The type or namespace name 'Xunit' could not be found"));

    /// <summary>A test that ran and reported a failing test is the same: it ran.</summary>
    [Fact]
    public void A_failing_test_is_a_real_failure()
        => Assert.False(ShellRefusal.NeverRan(
            "dotnet test",
            "Failed!  - Failed:     1, Passed:    62, Skipped:     0, Total:    63"));

    /// <summary>
    /// <c>exit /b 1</c> is the command <c>RecoveredByAnotherRouteTests</c> fails a step with. It
    /// runs. If this ever answered true, that test's boundary would be gone.
    /// </summary>
    [Fact]
    public void A_command_that_ran_and_chose_to_fail_is_a_real_failure()
        => Assert.False(ShellRefusal.NeverRan("exit /b 1", ""));

    /// <summary>
    /// No command text, no answer. <c>ProcessExec.RunAsync</c> starts a named executable with an
    /// argument list and has no line to check a refusal against, so it passes none — and a
    /// program that prints the phrase itself keeps being a program that failed.
    /// </summary>
    [Fact]
    public void Without_the_command_line_nothing_is_reclassified()
        => Assert.False(ShellRefusal.NeverRan(null, CmdRefusal));

    /// <summary>The refusal must actually be there; silence is not one.</summary>
    [Fact]
    public void Silence_is_not_a_refusal()
        => Assert.False(ShellRefusal.NeverRan("Select-String x", ""));

    /// <summary>
    /// Quoting and case are spellings, not different commands — the same reason
    /// <see cref="ShellOperation"/> lower-cases the program it names.
    /// </summary>
    [Fact]
    public void Case_and_quotes_around_the_head_do_not_hide_it()
        => Assert.True(ShellRefusal.NeverRan(
            "\"select-string\" -Path log.txt",
            "'Select-String' is not recognized as an internal or external command,"));

    /// <summary>A multi-line script: every statement is a head.</summary>
    [Fact]
    public void A_head_on_a_later_line_of_a_script_counts()
        => Assert.True(ShellRefusal.NeverRan(
            "cd src\nSet-Content out.txt 'x'",
            "'Set-Content' is not recognized as an internal or external command,"));
}
